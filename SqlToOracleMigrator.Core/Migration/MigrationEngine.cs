using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core.Tracking;

namespace SqlToOracleMigrator.Core;

public sealed class MigrationEngine
{
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

        // Ensure the tracking schema lives in the SOURCE database.
        try
        {
            openSql.ChangeDatabase(request.SourceDatabase);
        }
        catch
        {
            // If ChangeDatabase fails (rare), ToolMig operations and metadata queries will fail explicitly later.
        }

        await _toolMig.EnsureCreatedAsync(openSql, cancellationToken);

        // Create or resume ToolMig run
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
            request.ErrorHandlingMode
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
        var runDir = _paths.GetRunDirectory(runId, run.StartedAt.ToLocalTime());
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

        try
        {
            // Discover tables (source schema + table)
            Raise(MigrationStage.Discovery, "Discovering tables...");
            AppendConvertLog("[Discovery] Discovering tables...");
            var tables = await DiscoverTablesAsync(openSql, request.SourceDatabase, cancellationToken);
            summary.TableCount = tables.Count;

            Raise(MigrationStage.Planning, $"Planning migration for {tables.Count} tables.");
            AppendConvertLog($"[Planning] Tables discovered: {tables.Count}");

            // Determine schema mapping strategy
            string GetTargetSchema(string sourceSchema)
            {
                if (request.CloneSourceSchemas)
                    return OracleIdent.FormatSchema(sourceSchema);
                return OracleIdent.FormatSchema(request.TargetSchema);
            }

            // Schema provisioning
            if (!completedStages.Contains(MigrationStage.SchemaProvisioning.ToString()))
            {
                Raise(MigrationStage.SchemaProvisioning, "Provisioning target schemas/users...");
                AppendConvertLog("[SchemaProvisioning] Starting...");
                await ToolMigStageAsync(MigrationStage.SchemaProvisioning, "InProgress", "Provisioning schemas/users", 0);

                var errors = new List<StageError>();
                try
                {
                    if (request.CloneSourceSchemas)
                    {
                        var sourceSchemas = tables.Select(t => t.Schema)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        foreach (var s in sourceSchemas)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var targetSchema = GetTargetSchema(s);
                            await ToolMigObjectAsync(MigrationStage.SchemaProvisioning, s, s, "SCHEMA", "InProgress", null, null);
                            try
                            {
                                await _oraMeta.EnsureSchemaUserExistsAsync(openOra, targetSchema, request.AutoCreateTargetSchemas, cancellationToken);
                                await ToolMigObjectAsync(MigrationStage.SchemaProvisioning, s, s, "SCHEMA", "Completed", null, null);
                            }
                            catch (Exception ex)
                            {
                                errors.Add(StageError.FromException(MigrationStage.SchemaProvisioning, s, s, ex));
                                await ToolMigObjectAsync(MigrationStage.SchemaProvisioning, s, s, "SCHEMA", "Failed", ex.GetType().Name, ex.Message);
                                AppendConvertLog($"[SchemaProvisioning][ERROR] {s}: {ex.Message}");
                                if (stageMode == ErrorHandlingMode.FailFast) throw;
                            }
                        }
                    }
                    else
                    {
                        OracleMetadataProvider.ValidateOracleIdentifier(request.TargetSchema);
                        var normalizedTarget = OracleIdent.FormatSchema(request.TargetSchema);
                        await _oraMeta.EnsureSchemaUserExistsAsync(openOra, normalizedTarget, request.AutoCreateTargetSchemas, cancellationToken);
                    }

                    if (errors.Count > 0)
                        throw new StageFailedException(MigrationStage.SchemaProvisioning, errors);

                    await ToolMigStageAsync(MigrationStage.SchemaProvisioning, "Completed", "Schemas/users ready", 0);
                    AppendConvertLog("[SchemaProvisioning] Completed.");
                }
                catch (StageFailedException sfe)
                {
                    await ToolMigStageAsync(MigrationStage.SchemaProvisioning, "Failed", sfe.Message, sfe.Errors.Count);
                    WriteStageReport(runDir, sfe.Stage.ToString(), sfe.Errors);
                    throw;
                }
                catch (Exception ex)
                {
                    var errs = errors.Count > 0 ? errors : new List<StageError> { StageError.FromException(MigrationStage.SchemaProvisioning, "", "", ex) };
                    await ToolMigStageAsync(MigrationStage.SchemaProvisioning, "Failed", ex.Message, errs.Count);
                    WriteStageReport(runDir, MigrationStage.SchemaProvisioning.ToString(), errs);
                    throw;
                }
            }
            else
            {
                Raise(MigrationStage.SchemaProvisioning, "Skipping: already completed in prior run.");
                AppendConvertLog("[SchemaProvisioning] Skipped (already completed).");
            }

            // v6: Validate Definitions (DDL parse-only)
            if (request.EnableDataDefValidation)
            {
                if (!completedStages.Contains(MigrationStage.DataDefValidation.ToString()))
                {
                    Raise(MigrationStage.DataDefValidation, "Validating generated DDL (parse-only; no commit)...");
                    AppendConvertLog("[ValidateDefinitions] Starting...");
                    await ToolMigStageAsync(MigrationStage.DataDefValidation, "InProgress", "Validating DDL (parse-only)", 0);

                    var errors = new List<StageError>();
                    var completedObjects = request.ResumeRunId.HasValue
                        ? await _toolMig.GetCompletedObjectsAsync(openSql, runId, MigrationStage.DataDefValidation.ToString(), cancellationToken)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    try
                    {
                        var i = 0;
                        foreach (var t in tables)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            i++;
                            var key = $"{t.Schema}.{t.Table}";
                            if (completedObjects.Contains(key))
                                continue;

                            await ToolMigObjectAsync(MigrationStage.DataDefValidation, t.Schema, t.Table, "TABLE", "InProgress", null, null);
                            try
                            {
                                var columns = await _sqlMeta.GetTableColumnsAsync(openSql, request.SourceDatabase, t.Schema, t.Table, cancellationToken);
                                var ddl = OracleDdlGenerator.CreateTableDdl(GetTargetSchema(t.Schema), t.Table, columns, _typeMapper);
                                await _oraMeta.ValidateDdlAsync(openOra, ddl, cancellationToken);
                                await ToolMigObjectAsync(MigrationStage.DataDefValidation, t.Schema, t.Table, "TABLE", "Completed", null, null);
                            }
                            catch (Exception ex)
                            {
                                errors.Add(StageError.FromException(MigrationStage.DataDefValidation, t.Schema, t.Table, ex));
                                await ToolMigObjectAsync(MigrationStage.DataDefValidation, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                                AppendConvertLog($"[ValidateDefinitions][ERROR] {t.Schema}.{t.Table}: {ex.Message}");
                                if (stageMode == ErrorHandlingMode.FailFast) throw;
                            }

                            if (tables.Count > 0 && i % 50 == 0)
                                Raise(MigrationStage.DataDefValidation, $"Validated DDL for {i}/{tables.Count} tables...", (double)i / tables.Count);
                        }

                        if (errors.Count > 0)
                            throw new StageFailedException(MigrationStage.DataDefValidation, errors);

                        await ToolMigStageAsync(MigrationStage.DataDefValidation, "Completed", "DDL validation passed", 0);
                        AppendConvertLog("[ValidateDefinitions] Completed with no issues.");
                    }
                    catch (StageFailedException sfe)
                    {
                        await ToolMigStageAsync(MigrationStage.DataDefValidation, "Failed", sfe.Message, sfe.Errors.Count);
                        WriteStageReport(runDir, sfe.Stage.ToString(), sfe.Errors);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await ToolMigStageAsync(MigrationStage.DataDefValidation, "Failed", ex.Message, errors.Count);
                        WriteStageReport(runDir, MigrationStage.DataDefValidation.ToString(), errors.Count > 0
                            ? errors
                            : new List<StageError> { StageError.FromException(MigrationStage.DataDefValidation, "", "", ex) });
                        throw;
                    }
                }
                else
                {
                    Raise(MigrationStage.DataDefValidation, "Skipping: already completed in prior run.");
                    AppendConvertLog("[ValidateDefinitions] Skipped (already completed).");
                }
            }

            // DDL generation + deployment
            if (!completedStages.Contains(MigrationStage.DdlGeneration.ToString()))
            {
                Raise(MigrationStage.DdlGeneration, "Generating and deploying table DDL...");
                AppendConvertLog("[DdlGeneration] Starting...");
                await ToolMigStageAsync(MigrationStage.DdlGeneration, "InProgress", "Generating + deploying DDL", 0);

                var errors = new List<StageError>();
                var completedObjects = request.ResumeRunId.HasValue
                    ? await _toolMig.GetCompletedObjectsAsync(openSql, runId, MigrationStage.DdlGeneration.ToString(), cancellationToken)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    foreach (var t in tables)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var key = $"{t.Schema}.{t.Table}";
                        if (completedObjects.Contains(key))
                            continue;

                        await ToolMigObjectAsync(MigrationStage.DdlGeneration, t.Schema, t.Table, "TABLE", "InProgress", null, null);
                        try
                        {
                            await DeployTableAsync(openSql, openOra, request.SourceDatabase, t.Schema, t.Table, GetTargetSchema(t.Schema), cancellationToken);
                            await ToolMigObjectAsync(MigrationStage.DdlGeneration, t.Schema, t.Table, "TABLE", "Completed", null, null);
                        }
                        catch (Exception ex)
                        {
                            errors.Add(StageError.FromException(MigrationStage.DdlGeneration, t.Schema, t.Table, ex));
                            await ToolMigObjectAsync(MigrationStage.DdlGeneration, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                            AppendConvertLog($"[DdlGeneration][ERROR] {t.Schema}.{t.Table}: {ex.Message}");
                            if (stageMode == ErrorHandlingMode.FailFast) throw;
                        }
                    }

                    if (errors.Count > 0)
                        throw new StageFailedException(MigrationStage.DdlGeneration, errors);

                    await ToolMigStageAsync(MigrationStage.DdlGeneration, "Completed", "DDL deployed", 0);
                    AppendConvertLog("[DdlGeneration] Completed.");
                }
                catch (StageFailedException sfe)
                {
                    await ToolMigStageAsync(MigrationStage.DdlGeneration, "Failed", sfe.Message, sfe.Errors.Count);
                    WriteStageReport(runDir, sfe.Stage.ToString(), sfe.Errors);
                    throw;
                }
                catch (Exception ex)
                {
                    await ToolMigStageAsync(MigrationStage.DdlGeneration, "Failed", ex.Message, errors.Count);
                    WriteStageReport(runDir, MigrationStage.DdlGeneration.ToString(), errors.Count > 0
                        ? errors
                        : new List<StageError> { StageError.FromException(MigrationStage.DdlGeneration, "", "", ex) });
                    throw;
                }
            }
            else
            {
                Raise(MigrationStage.DdlGeneration, "Skipping: already completed in prior run.");
                AppendConvertLog("[DdlGeneration] Skipped (already completed).");
            }

            // Validate Data (dry-run inserts / rollback)
            if (request.EnableDataValidation)
            {
                if (!completedStages.Contains(MigrationStage.DataValidation.ToString()))
                {
                    var limit = request.ValidateFullDataset ? int.MaxValue : Math.Max(0, request.DataValidationRowLimit);
                    if (limit <= 0 && !request.ValidateFullDataset)
                    {
                        await ToolMigStageAsync(MigrationStage.DataValidation, "Skipped", "Row limit <= 0", 0);
                        _logger.Info("[DataValidation] Skipped (row limit <= 0)." );
                        AppendConvertLog("[ValidateData] Skipped (row limit <= 0)." );
                    }
                    else
                    {
                        Raise(MigrationStage.DataValidation, request.ValidateFullDataset
                            ? "Validating data migration (full dataset; rollback)..."
                            : $"Validating data migration (top {limit:N0} rows/table; rollback)...");

                        AppendConvertLog(request.ValidateFullDataset
                            ? "[ValidateData] Starting (FULL dataset; rollback)..."
                            : $"[ValidateData] Starting (TOP {limit:N0} rows/table; rollback)...");

                        await ToolMigStageAsync(MigrationStage.DataValidation, "InProgress", "Validating data (dry-run)", 0);

                        var errors = new List<StageError>();
                        var completedObjects = request.ResumeRunId.HasValue
                            ? await _toolMig.GetCompletedObjectsAsync(openSql, runId, MigrationStage.DataValidation.ToString(), cancellationToken)
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        try
                        {
                            var i = 0;
                            foreach (var t in tables)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                i++;
                                var key = $"{t.Schema}.{t.Table}";
                                if (completedObjects.Contains(key))
                                    continue;

                                await ToolMigObjectAsync(MigrationStage.DataValidation, t.Schema, t.Table, "TABLE", "InProgress", null, null);
                                try
                                {
                                    await ValidateTableDataAsync(openSql, openOra, request.SourceDatabase, t.Schema, t.Table, GetTargetSchema(t.Schema), request.ValidateFullDataset, limit, cancellationToken);
                                    await ToolMigObjectAsync(MigrationStage.DataValidation, t.Schema, t.Table, "TABLE", "Completed", null, null);
                                }
                                catch (Exception ex)
                                {
                                    errors.Add(StageError.FromException(MigrationStage.DataValidation, t.Schema, t.Table, ex));
                                    await ToolMigObjectAsync(MigrationStage.DataValidation, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                                    AppendConvertLog($"[ValidateData][ERROR] {t.Schema}.{t.Table}: {ex.Message}");
                                    if (stageMode == ErrorHandlingMode.FailFast) throw;
                                }

                                if (tables.Count > 0 && i % 25 == 0)
                                    Raise(MigrationStage.DataValidation, $"Validated data for {i}/{tables.Count} tables...", (double)i / tables.Count);
                            }

                            if (errors.Count > 0)
                                throw new StageFailedException(MigrationStage.DataValidation, errors);

                            await ToolMigStageAsync(MigrationStage.DataValidation, "Completed", "Data validation passed", 0);
                            AppendConvertLog("[ValidateData] Completed with no issues.");
                        }
                        catch (StageFailedException sfe)
                        {
                            await ToolMigStageAsync(MigrationStage.DataValidation, "Failed", sfe.Message, sfe.Errors.Count);
                            WriteStageReport(runDir, sfe.Stage.ToString(), sfe.Errors);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            await ToolMigStageAsync(MigrationStage.DataValidation, "Failed", ex.Message, errors.Count);
                            WriteStageReport(runDir, MigrationStage.DataValidation.ToString(), errors.Count > 0
                                ? errors
                                : new List<StageError> { StageError.FromException(MigrationStage.DataValidation, "", "", ex) });
                            throw;
                        }
                    }
                }
                else
                {
                    Raise(MigrationStage.DataValidation, "Skipping: already completed in prior run.");
                    AppendConvertLog("[ValidateData] Skipped (already completed).");
                }
            }

            // Data migration (parallel)
            if (!completedStages.Contains(MigrationStage.DataMigration.ToString()))
            {
                Raise(MigrationStage.DataMigration, $"Migrating table data in parallel (DOP={dop})...");
                AppendConvertLog($"[DataMigration] Starting (DOP={dop})...");
                await ToolMigStageAsync(MigrationStage.DataMigration, "InProgress", $"Data migration (DOP={dop})", 0);

                var completedObjects = request.ResumeRunId.HasValue
                    ? await _toolMig.GetCompletedObjectsAsync(openSql, runId, MigrationStage.DataMigration.ToString(), cancellationToken)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var toMigrate = tables
                    .Where(t => !completedObjects.Contains($"{t.Schema}.{t.Table}"))
                    .ToList();

                var errors = new ConcurrentBag<StageError>();
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = linkedCts.Token;
                var semaphore = new SemaphoreSlim(dop, dop);
                var tasks = new List<Task>();

                int completed = 0;
                foreach (var t in toMigrate)
                {
                    await semaphore.WaitAsync(token);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            await ToolMigObjectAsync(MigrationStage.DataMigration, t.Schema, t.Table, "TABLE", "InProgress", null, null);
                            await CopyTableAsync(openSql, openOra, request.SourceDatabase, t.Schema, t.Table, GetTargetSchema(t.Schema), token);
                            await ToolMigObjectAsync(MigrationStage.DataMigration, t.Schema, t.Table, "TABLE", "Completed", null, null);
                        }
                        catch (Exception ex)
                        {
                            errors.Add(StageError.FromException(MigrationStage.DataMigration, t.Schema, t.Table, ex));
                            await ToolMigObjectAsync(MigrationStage.DataMigration, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                            _logger.Error($"Data migration failed for {t.Schema}.{t.Table}", ex);
                            AppendConvertLog($"[DataMigration][ERROR] {t.Schema}.{t.Table}: {ex.Message}");

                            if (stageMode == ErrorHandlingMode.FailFast)
                            {
                                try { linkedCts.Cancel(); } catch { }
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                            var done = Interlocked.Increment(ref completed);
                            var pct = toMigrate.Count == 0 ? 1.0 : (double)done / toMigrate.Count;
                            Raise(MigrationStage.DataMigration, $"Completed {done}/{toMigrate.Count} tables.", pct);
                        }
                    }, token));
                }

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch
                {
                    // Swallow aggregate exceptions; we rely on errors bag + cancellation.
                }

                if (!errors.IsEmpty)
                {
                    var list = errors.ToList();
                    await ToolMigStageAsync(MigrationStage.DataMigration, "Failed", $"Data migration failed with {list.Count} error(s)", list.Count);
                    WriteStageReport(runDir, MigrationStage.DataMigration.ToString(), list);
                    throw new StageFailedException(MigrationStage.DataMigration, list);
                }

                await ToolMigStageAsync(MigrationStage.DataMigration, "Completed", "Data migrated", 0);
                AppendConvertLog("[DataMigration] Completed.");
            }
            else
            {
                Raise(MigrationStage.DataMigration, "Skipping: already completed in prior run.");
                AppendConvertLog("[DataMigration] Skipped (already completed).");
            }

            // Post validation (row counts, best-effort)
            if (!completedStages.Contains(MigrationStage.PostValidation.ToString()))
            {
                Raise(MigrationStage.PostValidation, "Running basic row-count validation (first 50 tables)...");
                AppendConvertLog("[PostValidation] Starting row-count validation (first 50 tables)...");
                await ToolMigStageAsync(MigrationStage.PostValidation, "InProgress", "Row-count validation", 0);

                var errors = new List<StageError>();
                foreach (var t in tables.Take(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var srcCount = await _sqlMeta.GetTableRowCountAsync(openSql, request.SourceDatabase, t.Schema, t.Table, cancellationToken);
                        var tgtCount = await GetOracleTableRowCountAsync(openOra, GetTargetSchema(t.Schema), t.Table, cancellationToken);

                        if (srcCount != tgtCount)
                        {
                            var msg = $"Row count mismatch for {t.Schema}.{t.Table}: SQL={srcCount}, Oracle={tgtCount}";
                            _logger.Warn(msg);
                            AppendConvertLog($"[PostValidation][WARN] {msg}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(StageError.FromException(MigrationStage.PostValidation, t.Schema, t.Table, ex));
                        _logger.Warn($"Validation failed for {t.Schema}.{t.Table}: {ex.Message}");
                        AppendConvertLog($"[PostValidation][WARN] {t.Schema}.{t.Table}: {ex.Message}");
                        if (stageMode == ErrorHandlingMode.FailFast) break;
                    }
                }

                // PostValidation is best-effort: never fail overall run.
                await ToolMigStageAsync(MigrationStage.PostValidation, "Completed", errors.Count == 0 ? "Post validation complete" : $"Post validation complete with {errors.Count} warning(s)", errors.Count);
                if (errors.Count > 0)
                    WriteStageReport(runDir, MigrationStage.PostValidation.ToString(), errors);
            }
            else
            {
                Raise(MigrationStage.PostValidation, "Skipping: already completed in prior run.");
                AppendConvertLog("[PostValidation] Skipped (already completed).");
            }

            summary.CompletedUtc = DateTimeOffset.UtcNow;
            Raise(MigrationStage.Finalization, "Writing run summary...");
            AppendConvertLog("[Finalization] Writing run summary...");
            await ToolMigStageAsync(MigrationStage.Finalization, "InProgress", "Finalizing", 0);

            WriteRunSummary(runDir, summary);
            await ToolMigStageAsync(MigrationStage.Finalization, "Completed", "Completed", 0);

            await _toolMig.MarkRunCompletedAsync(openSql, runId, success: true, CancellationToken.None);
            AppendConvertLog("[Finalization] Migration run completed.");

            Raise(MigrationStage.Finalization, "Migration run completed.");
            _logger.Info("Migration completed.");
        }
        catch
        {
            try
            {
                await _toolMig.MarkRunCompletedAsync(openSql, runId, success: false, CancellationToken.None);
            }
            catch
            {
                // ignore
            }

            AppendConvertLog("[Finalization] Migration run FAILED.");
            throw;
        }
    }

    private void ValidateResumeCompatibility(ToolMigRunInfo run, string currentRequestJson)
    {
        if (string.IsNullOrWhiteSpace(run.RequestJson))
            return;

        try
        {
            using var prior = JsonDocument.Parse(run.RequestJson);
            using var curr = JsonDocument.Parse(currentRequestJson);

            // Best-effort check: warn on mismatched critical flags.
            string? Get(JsonDocument d, string prop)
            {
                if (d.RootElement.TryGetProperty(prop, out var p))
                    return p.ToString();
                return null;
            }

            var pairs = new[]
            {
                ("SourceDatabase", "SourceDatabase"),
                ("CloneSourceSchemas", "CloneSourceSchemas"),
                ("AutoCreateTargetSchemas", "AutoCreateTargetSchemas"),
                ("EnableDataDefValidation", "EnableDataDefValidation"),
                ("EnableDataValidation", "EnableDataValidation"),
                ("DataValidationRowLimit", "DataValidationRowLimit"),
                ("ValidateFullDataset", "ValidateFullDataset")
            };

            foreach (var (p, c) in pairs)
            {
                var v1 = Get(prior, p);
                var v2 = Get(curr, c);
                if (v1 is null || v2 is null) continue;
                if (!string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase))
                    _logger.Warn($"[ToolMig] Resume: request option '{p}' differs (prior={v1}, current={v2}). Resume will proceed, but results may be inconsistent.");
            }
        }
        catch
        {
            // ignore parse issues
        }
    }

    private void WriteMasterTemplateArtifacts(ToolMigRunInfo run, MigrationRequest request, string requestJson, string runDir, Action<string> appendLog)
    {
        try
        {
            // Always ensure convert log exists.
            File.AppendAllText(Path.Combine(runDir, "Convert_ToOracle.log"), $"{DateTimeOffset.Now:O} [Run] RunId={run.RunId} Version={run.Version} SourceDb={run.SourceDatabase}{Environment.NewLine}");

            var templatePath = Path.Combine(_paths.TemplatesDirectory, "DB_Conversion_MasterTemplate_v1.txt");
            if (!File.Exists(templatePath))
            {
                appendLog("[Template] DB_Conversion_MasterTemplate_v1.txt not found; skipping master script generation.");
                return;
            }

            var template = File.ReadAllText(templatePath);
            var rendered = template
                .Replace("{{RUN_ID}}", run.RunId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                .Replace("{{RUN_VERSION}}", run.Version.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{SOURCE_DB}}", request.SourceDatabase, StringComparison.OrdinalIgnoreCase)
                .Replace("{{TARGET_SCHEMA}}", request.TargetSchema, StringComparison.OrdinalIgnoreCase)
                .Replace("{{CLONE_SOURCE_SCHEMAS}}", request.CloneSourceSchemas.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{AUTO_CREATE_TARGET_SCHEMAS}}", request.AutoCreateTargetSchemas.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{VALIDATE_DEFINITIONS}}", request.EnableDataDefValidation.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{VALIDATE_DATA}}", request.EnableDataValidation.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{DATA_VALIDATION_ROW_LIMIT}}", request.DataValidationRowLimit.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{VALIDATE_FULL_DATASET}}", request.ValidateFullDataset.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{DEGREE_OF_PARALLELISM}}", request.DegreeOfParallelism.ToString(), StringComparison.OrdinalIgnoreCase);

            var outMaster = Path.Combine(runDir, "master_generated.sql");
            File.WriteAllText(outMaster, rendered, Encoding.UTF8);

            var outReq = Path.Combine(runDir, "request_snapshot.json");
            File.WriteAllText(outReq, requestJson, Encoding.UTF8);

            appendLog($"[Template] Generated master script: {outMaster}");
            appendLog($"[Template] Saved request snapshot: {outReq}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Template] Failed to generate master artifacts: {ex.Message}");
        }
    }

    private async Task<List<(string Schema, string Table)>> DiscoverTablesAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var query = _queries.Format("ListSqlTables", new Dictionary<string, string> { ["db"] = db });

        await using var cmd = new SqlCommand(query, openSql);
        var list = new List<(string Schema, string Table)>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            list.Add((rdr.GetString(0), rdr.GetString(1)));
        }
        return list;
    }

    private async Task DeployTableAsync(SqlConnection openSql, OracleConnection openOra, string dbName, string schema, string table, string targetSchema, CancellationToken cancellationToken)
    {
        var columns = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, cancellationToken);
        var ddl = OracleDdlGenerator.CreateTableDdl(targetSchema, table, columns, _typeMapper);

        // IMPORTANT: Do not quote normal usernames/schemas (e.g., SYSTEM). Quoted usernames become case-sensitive and often fail.
        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var drop = $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {schemaPrefix}.{OracleIdent.QuoteIdent(table)}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using (var dropCmd = new OracleCommand(drop, openOra))
        {
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var cmd = new OracleCommand(ddl, openOra);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.Info($"Deployed table DDL: {schema}.{table}");
    }

    private async Task CopyTableAsync(SqlConnection openSql, OracleConnection openOra, string dbName, string schema, string table, string targetSchema, CancellationToken cancellationToken)
    {
        var columns = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, cancellationToken);
        if (columns.Count == 0) return;

        var colNames = columns.OrderBy(c => c.Ordinal).Select(c => c.ColumnName).ToList();

        var db = SqlIdent.Bracket(dbName);
        var selectSql = _queries.Format("SqlSelectTableAll", new Dictionary<string, string> { ["db"] = db });

        await using var selectCmd = new SqlCommand(selectSql, openSql);
        selectCmd.Parameters.AddWithValue("@SchemaName", schema);
        selectCmd.Parameters.AddWithValue("@TableName", table);
        selectCmd.CommandTimeout = 0;

        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var insertSql = $"INSERT INTO {schemaPrefix}.{OracleIdent.QuoteIdent(table)} ({string.Join(",", colNames.Select(OracleIdent.QuoteIdent))}) VALUES ({string.Join(",", colNames.Select((c, i) => $":p{i}"))})";

        await using var txn = openOra.BeginTransaction();
        await using var insertCmd = new OracleCommand(insertSql, openOra)
        {
            Transaction = txn,
            BindByName = false
        };

        insertCmd.Parameters.Clear();
        for (var i = 0; i < colNames.Count; i++)
            insertCmd.Parameters.Add(new OracleParameter($"p{i}", DBNull.Value));

        const int batchCommit = 2000;
        var pending = 0;

        await using var rdr = await selectCmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < colNames.Count; i++)
            {
                var val = rdr.IsDBNull(i) ? DBNull.Value : rdr.GetValue(i);
                insertCmd.Parameters[i].Value = val;
            }

            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            pending++;

            if (pending >= batchCommit)
            {
                await insertCmd.Transaction!.CommitAsync(cancellationToken);
                pending = 0;

                // Begin new transaction
                insertCmd.Transaction!.Dispose();
                insertCmd.Transaction = openOra.BeginTransaction();
            }
        }

        if (pending > 0)
            await insertCmd.Transaction!.CommitAsync(cancellationToken);
    }

    private async Task ValidateTableDataAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string schema,
        string table,
        string targetSchema,
        bool validateFullDataset,
        int rowLimit,
        CancellationToken cancellationToken)
    {
        var columns = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, cancellationToken);
        if (columns.Count == 0) return;

        var colNames = columns.OrderBy(c => c.Ordinal).Select(c => c.ColumnName).ToList();

        var db = SqlIdent.Bracket(dbName);

        string selectSql;
        if (validateFullDataset)
        {
            selectSql = _queries.Format("SqlSelectTableAll", new Dictionary<string, string> { ["db"] = db });
        }
        else
        {
            selectSql = _queries.Format("SqlSelectTableTopN", new Dictionary<string, string> { ["db"] = db });
        }

        await using var selectCmd = new SqlCommand(selectSql, openSql);
        selectCmd.Parameters.AddWithValue("@SchemaName", schema);
        selectCmd.Parameters.AddWithValue("@TableName", table);
        if (!validateFullDataset)
            selectCmd.Parameters.AddWithValue("@TopN", Math.Max(1, rowLimit));

        selectCmd.CommandTimeout = 0;

        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var insertSql = $"INSERT INTO {schemaPrefix}.{OracleIdent.QuoteIdent(table)} ({string.Join(",", colNames.Select(OracleIdent.QuoteIdent))}) VALUES ({string.Join(",", colNames.Select((c, i) => $":p{i}"))})";

        await using var txn = openOra.BeginTransaction();
        await using var insertCmd = new OracleCommand(insertSql, openOra)
        {
            Transaction = txn,
            BindByName = false
        };

        insertCmd.Parameters.Clear();
        for (var i = 0; i < colNames.Count; i++)
            insertCmd.Parameters.Add(new OracleParameter($"p{i}", DBNull.Value));

        var written = 0;
        await using var rdr = await selectCmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < colNames.Count; i++)
            {
                var val = rdr.IsDBNull(i) ? DBNull.Value : rdr.GetValue(i);
                insertCmd.Parameters[i].Value = val;
            }

            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            written++;

            if (!validateFullDataset && written >= rowLimit)
                break;
        }

        // Rollback always (dry-run)
        try
        {
            await txn.RollbackAsync(cancellationToken);
        }
        catch
        {
            // ignore rollback exceptions (best-effort)
        }

        _logger.Info($"[DataValidation] Dry-run inserted {written} row(s) for {schema}.{table} into {schemaPrefix}.{table} (rolled back)." );
    }

    private static async Task<long> GetOracleTableRowCountAsync(OracleConnection openOra, string schema, string table, CancellationToken cancellationToken)
    {
        var schemaPrefix = OracleIdent.FormatSchema(schema);
        var sql = $"SELECT COUNT(*) FROM {schemaPrefix}.{OracleIdent.QuoteIdent(table)}";
        await using var cmd = new OracleCommand(sql, openOra);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private void WriteStageReport(string runDir, string stageName, IReadOnlyList<StageError> issues)
    {
        try
        {
            var fileTxt = Path.Combine(runDir, $"{stageName}_errors.txt");
            var fileJson = Path.Combine(runDir, $"{stageName}_errors.json");

            if (issues.Count == 0)
            {
                File.WriteAllText(fileTxt, $"{stageName}: No issues detected.{Environment.NewLine}");
                File.WriteAllText(fileJson, "[]");
                return;
            }

            var lines = new List<string>
            {
                $"{stageName}: {issues.Count} error(s) detected.",
                "------------------------------------------------------------"
            };
            lines.AddRange(issues.Select(e => $"{e.Schema}.{e.Object}: {e.ErrorType}: {e.Message}"));
            File.WriteAllLines(fileTxt, lines);
            File.WriteAllText(fileJson, JsonSerializer.Serialize(issues, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to write {stageName} report: {ex.Message}");
        }
    }

    private void WriteRunSummary(string runDir, MigrationRunSummary summary)
    {
        try
        {
            var file = Path.Combine(runDir, "run_summary.json");
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file, json);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to write run summary: {ex.Message}");
        }
    }

    private void Raise(MigrationStage stage, string message, double? pct = null)
    {
        _logger.Info($"[{stage}] {message}");
        Progress?.Invoke(this, new MigrationProgress(stage, message, pct));
    }

    private sealed class MigrationRunSummary
    {
        public Guid RunId { get; set; }
        public int RunVersion { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public string SourceConnection { get; set; } = "";
        public string SourceDatabase { get; set; } = "";
        public string TargetConnection { get; set; } = "";
        public string TargetSchema { get; set; } = "";
        public int DegreeOfParallelism { get; set; }
        public int TableCount { get; set; }
        public string ErrorHandlingMode { get; set; } = "FailFast";
    }

    private sealed record StageError(string Stage, string Schema, string Object, string ErrorType, string Message, string? Details)
    {
        public static StageError FromException(MigrationStage stage, string schema, string obj, Exception ex)
        {
            return new StageError(stage.ToString(), schema, obj, ex.GetType().Name, ex.Message, ex.ToString());
        }
    }

    private sealed class StageFailedException : Exception
    {
        public MigrationStage Stage { get; }
        public List<StageError> Errors { get; }

        public StageFailedException(MigrationStage stage, List<StageError> errors)
            : base($"{stage} failed with {errors.Count} error(s).")
        {
            Stage = stage;
            Errors = errors;
        }
    }
}
