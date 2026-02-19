using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using SqlToOracleMigrator.Core.Migration;
using SqlToOracleMigrator.Core.Tracking;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    // Exposed for Desktop UI to link to the most recent run artifacts (RunSummary.html, reports, etc.).
    public Guid? LastRunId { get; private set; }
    public string? LastRunDirectory { get; private set; }

    private readonly ConnectionManager _connMgr;
    private readonly SqlServerMetadataProvider _sqlMeta;
    private readonly OracleMetadataProvider _oraMeta;
    private readonly SqlToOracleTypeMapper _typeMapper;
    private readonly ISqlQueryStore _queries;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private readonly ToolMigRepository _toolMig;

    public event EventHandler<MigrationProgress>? Progress;

    public MigrationEngine(
        ConnectionManager connMgr,
        SqlServerMetadataProvider sqlMeta,
        OracleMetadataProvider oraMeta,
        SqlToOracleTypeMapper typeMapper,
        ISqlQueryStore queries,
        IAppLogger logger,
        AppPaths paths,
        ToolMigRepository toolMig)
    {
        _connMgr = connMgr ?? throw new ArgumentNullException(nameof(connMgr));
        _sqlMeta = sqlMeta ?? throw new ArgumentNullException(nameof(sqlMeta));
        _oraMeta = oraMeta ?? throw new ArgumentNullException(nameof(oraMeta));
        _typeMapper = typeMapper ?? throw new ArgumentNullException(nameof(typeMapper));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _toolMig = toolMig ?? throw new ArgumentNullException(nameof(toolMig));
    }

    public async Task RunDatabaseMigrationAsync(MigrationRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // v1.1: provide request settings to internal helpers (e.g., OracleBulkCopy options).
        SetRequestAccessor(() => request);

        var dop = Math.Clamp(request.DegreeOfParallelism, 1, 32);
        var stageMode = request.ErrorHandlingMode;

        _logger.Info($"Migration requested: SQL '{request.SourceSqlConnection.Name}' DB '{request.SourceDatabase}' => Oracle '{request.TargetOracleConnection.Name}' (cloneSchemas={request.CloneSourceSchemas}), DOP={dop}, Mode={stageMode}.");
        Raise(MigrationStage.Discovery, "Connecting and discovering source/target metadata...");

        if (!_connMgr.IsConnected(request.SourceSqlConnection))
        {
            var (ok, msg) = await _connMgr.ConnectAsync(request.SourceSqlConnection, cancellationToken);
            if (!ok) throw new InvalidOperationException(msg);
        }

        if (!_connMgr.IsConnected(request.TargetOracleConnection))
        {
            var (ok, msg) = await _connMgr.ConnectAsync(request.TargetOracleConnection, cancellationToken);
            if (!ok) throw new InvalidOperationException(msg);
        }

        var openSql = _connMgr.TryGetOpenSql(request.SourceSqlConnection.Name)
            ?? throw new InvalidOperationException("Source SQL connection is not active.");

        var openOra = _connMgr.TryGetOpenOracle(request.TargetOracleConnection.Name)
            ?? throw new InvalidOperationException("Target Oracle connection is not active.");

        // Optional: ensure/switch to a target PDB (Adventureworks2025) when SYSDBA is used.
        if (request.EnsureTargetPdb && !string.IsNullOrWhiteSpace(request.TargetPdbName))
        {
            Exception? pdbEx = null;
            try
            {
                _logger.Info($"[Oracle] EnsureTargetPdb=true. Ensuring/switching to PDB '{request.TargetPdbName}'...");
                await EnsureAndSwitchToPdbAsync(
                    openOra,
                    request.TargetPdbName,
                    request.TargetOracleConnection.RuntimePassword ?? string.Empty,
                    request.DropTargetPdbIfExists,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                pdbEx = ex;
                _logger.Warn($"[Oracle] PDB ensure/switch failed: {ex.Message}");
            }

            // v6.3 guardrail: do not proceed with schema/object creation in CDB$ROOT.
            try
            {
                var conName = await GetOracleContainerNameAsync(openOra, cancellationToken);
                if (string.Equals(conName, "CDB$ROOT", StringComparison.OrdinalIgnoreCase))
                {
                    var msg = "Connected to Oracle container 'CDB$ROOT'. A PDB is required for this migration. " +
                              "The wizard can start from an XE/root connection, but PDB ensure/switch must succeed. " +
                              "Please connect using a PDB service (e.g., XEPDB1) OR configure DB_CREATE_FILE_DEST / FILE_NAME_CONVERT permissions so the tool can create/switch to the target PDB, then retry.";

                    if (pdbEx is not null)
                        msg += $" (PDB error: {pdbEx.Message})";

                    throw new InvalidOperationException(msg);
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch
            {
                // If we cannot determine container name, continue (best effort).
            }
        }

        try { openSql.ChangeDatabase(request.SourceDatabase); } catch { }

        await _toolMig.EnsureCreatedAsync(openSql, cancellationToken);

        // Request snapshot
        var requestSnapshotJson = JsonSerializer.Serialize(new
        {
            SourceConnection = request.SourceSqlConnection.Name,
            request.SourceDatabase,
            TargetConnection = request.TargetOracleConnection.Name,
            request.TargetSchema,
            request.CloneSourceSchemas,
            request.AutoCreateTargetSchemas,
            request.EnableDataDefValidation,
            request.EnableDataValidation,
            request.DataValidationRowLimit,
            request.ValidateFullDataset,
            request.DegreeOfParallelism,
            request.ErrorHandlingMode,

            // v6.3
            request.OverrideTargetObjectsEachRun,
            request.EnsureTargetPdb,
            request.TargetPdbName,
            request.CreateDependentObjects,
            request.CreateDependentObjectStubs,
            request.CreateForeignKeys,
            request.ForeignKeysEnableNoValidate
        }, new JsonSerializerOptions { WriteIndented = true });

        ToolMigRunInfo run;
        if (request.ResumeRunId.HasValue)
        {
            run = await _toolMig.GetRunAsync(openSql, request.ResumeRunId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Resume requested but ToolMig run '{request.ResumeRunId}' was not found in source DB '{request.SourceDatabase}'.");

            if (!string.Equals(run.SourceDatabase, request.SourceDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Resume run '{run.RunId}' belongs to source DB '{run.SourceDatabase}', but current request is '{request.SourceDatabase}'.");

            ValidateResumeCompatibility(run, requestSnapshotJson);
            _logger.Info($"[ToolMig] Resuming run {run.RunId} (v{run.Version}) for DB '{run.SourceDatabase}'.");
        }
        else
        {
            run = await _toolMig.CreateNewRunAsync(openSql, request.SourceDatabase, request.TargetSchema, requestSnapshotJson, cancellationToken);
        }

        var runId = run.RunId;
        LastRunId = runId;
        var runDir = _paths.GetRunDirectory(runId, run.StartedAt.ToLocalTime());
        LastRunDirectory = runDir;
        Directory.CreateDirectory(runDir);

        var convertLogPath = Path.Combine(runDir, "Convert_ToOracle.log");
        var logLock = new object();
        void AppendConvertLog(string line)
        {
            lock (logLock)
            {
                File.AppendAllText(convertLogPath, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
            }
        }

        WriteMasterTemplateArtifacts(run, request, requestSnapshotJson, runDir, AppendConvertLog);

        var completedStages = request.ResumeRunId.HasValue
            ? await _toolMig.GetCompletedStagesAsync(openSql, runId, cancellationToken)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toolMigGate = new SemaphoreSlim(1, 1);
        async Task ToolMigStageAsync(MigrationStage stage, string status, string? message, int errorCount)
        {
            await toolMigGate.WaitAsync(cancellationToken);
            try
            {
                await _toolMig.UpsertStageAsync(openSql, runId, stage.ToString(), status, message, errorCount, cancellationToken);
            }
            finally
            {
                toolMigGate.Release();
            }
        }

        async Task ToolMigObjectAsync(MigrationStage stage, string schema, string obj, string objType, string status, string? code, string? msg)
        {
            await toolMigGate.WaitAsync(cancellationToken);
            try
            {
                await _toolMig.UpsertObjectAsync(openSql, runId, stage.ToString(), schema, obj, objType, status, code, msg, cancellationToken);
            }
            finally
            {
                toolMigGate.Release();
            }
        }

        var summary = new MigrationRunSummary
        {
            RunId = runId,
            RunVersion = run.Version,
            StartedUtc = DateTimeOffset.UtcNow,
            SourceConnection = request.SourceSqlConnection.Name,
            SourceDatabase = request.SourceDatabase,
            TargetConnection = request.TargetOracleConnection.Name,
            TargetSchema = request.TargetSchema,
            DegreeOfParallelism = dop,
            ErrorHandlingMode = stageMode.ToString()
        };

        // Determine schema mapping strategy
        string GetTargetSchema(string sourceSchema)
        {
            if (request.CloneSourceSchemas)
                return OracleIdent.FormatSchema(sourceSchema);
            return OracleIdent.FormatSchema(request.TargetSchema);
        }

        var ctx = new MigrationContext(
            Engine: this,
            Request: request,
            OpenSql: openSql,
            OpenOra: openOra,
            Run: run,
            RunDir: runDir,
            AppendLog: AppendConvertLog,
            CompletedStages: completedStages,
            GetTargetSchema: GetTargetSchema,
            ToolMigStageAsync: ToolMigStageAsync,
            ToolMigObjectAsync: ToolMigObjectAsync,
            Summary: summary,
            Dop: dop,
            StageMode: stageMode);

        var runners = BuildStageRunners(ctx);

        static string PhaseNameForStage(MigrationStage stage)
        {
            return stage switch
            {
                MigrationStage.ConnectionFingerprinting or MigrationStage.DeepDiscovery or MigrationStage.PlanningTopologicalGraph => "Assess & Plan",
                MigrationStage.Provisioning or MigrationStage.DdlGenerationDryRun or MigrationStage.DeploymentSkeleton => "Schema Build",
                MigrationStage.DataStrategySampling or MigrationStage.ParallelDataMigration => "Data Prep",
                MigrationStage.PostLoadEnforcement or MigrationStage.FinalVerification => "Execute & Verify",
                _ => "Unknown"
            };
        }

        static bool IsPhaseTerminalStage(MigrationStage stage)
            => stage is MigrationStage.PlanningTopologicalGraph
                      or MigrationStage.DeploymentSkeleton
                      or MigrationStage.ParallelDataMigration
                      or MigrationStage.FinalVerification;

        async Task UpsertPhaseAsync(string phase, string status, string? message, int errorCount, int confidence)
        {
            if (phase == "Unknown") return;
            try
            {
                await _toolMig.UpsertGroupStatusAsync(openSql, runId, phase, status, message, errorCount, confidence, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warn( "Failed to update GroupStatus for {phase}: {ex.Message}");
            }
        }

        try
        {
            foreach (var r in runners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var phase = PhaseNameForStage(r.Stage);
                await UpsertPhaseAsync(phase, "InProgress",  "Running {r.Stage}", 0, ctx.Summary.Confidence);

                await r.RunAsync(ctx, cancellationToken);

                if (IsPhaseTerminalStage(r.Stage))
                {
                    await UpsertPhaseAsync(phase, "Completed",  "Phase completed at {r.Stage}", 0, ctx.Summary.Confidence);
                }
            }

            // Mark run completed
            await _toolMig.MarkRunCompletedAsync(openSql, runId, success: true, CancellationToken.None);
            AppendConvertLog("[Finalization] Migration run completed.");
            Raise(MigrationStage.Finalization, "Migration run completed.");
            _logger.Info("Migration completed.");
        }
        catch
        {
            try { await _toolMig.MarkRunCompletedAsync(openSql, runId, success: false, CancellationToken.None); } catch { }
            AppendConvertLog("[Finalization] Migration run FAILED.");
            throw;
        }
    }

    private static async Task<string> GetOracleContainerNameAsync(OracleConnection openOra, CancellationToken ct)
    {
        if (openOra is null) throw new ArgumentNullException(nameof(openOra));
        await using var cmd = openOra.CreateCommand();
        cmd.CommandText = "SELECT sys_context('USERENV','CON_NAME') FROM dual";
        var val = await cmd.ExecuteScalarAsync(ct);
        return (val?.ToString() ?? string.Empty).Trim();
    }
}