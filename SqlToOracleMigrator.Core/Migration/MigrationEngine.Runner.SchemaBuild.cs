using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core.Migration;
using SqlToOracleMigrator.Core.Migration.AssessAndPlan;
using SqlToOracleMigrator.Core.Oracle;
using SqlToOracleMigrator.Core.Tracking;
using System.Text;
using System.Text.Json;
using System.IO.Compression;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Phase 2 – Schema Build (Stages 4-6)
    /// Stage 5: DDL Validation
    /// </summary>
    private sealed class SchemaBuildDdlValidationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DdlGenerationDryRun;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(Stage.ToString()))
            {
                ctx.Engine.Raise(Stage, "Skipping: already completed in prior run.");
                ctx.AppendLog($"[{Stage}] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(Stage, "Generating DDL scripts and validating syntax (DBMS_SQL.PARSE)...");
            ctx.AppendLog($"[{Stage}] Starting...");
            await ctx.ToolMigStageAsync(Stage, "InProgress", "Generating + validating DDL", 0);

            var errors = new List<StageError>();
            var findings = new List<PreflightFinding>();

            try
            {
                // Compose DDL script bundle.
                var composer = new SchemaBuildDdlComposer(ctx.Engine);
                var bundle = await composer.ComposeAsync(ctx, ct);

                // Guardrail: if discovery found objects but composer produced no statements, fail early.
                if (ctx.Tables.Count > 0 && bundle.Statements.Count == 0)
                {
                    errors.Add(new StageError(Stage.ToString(), "", "", "NoDdlGenerated",
                        $"DDL composer produced 0 statements for {ctx.Tables.Count} discovered table(s). This would cause ORA-00942 during data load.",
                        "Ensure discovery populated ctx.Tables and SchemaBuildDdlComposer is generating table DDL."));
                    throw new StageFailedException(Stage, errors);
                }

                // Persist combined DDL to the run folder for easy inspection.
                // (ToolMig artifacts are great, but end users expect a file next to the logs.)
                try
                {
                    var ddlPath = Path.Combine(ctx.RunDir, "SchemaBuild_DDL.sql");
                    await File.WriteAllTextAsync(ddlPath, bundle.CombinedSql ?? string.Empty, ct);

                    // Also create a lightweight zip for easy sharing.
                    var zipPath = Path.Combine(ctx.RunDir, "SchemaBuild_DDL.zip");
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    {
                        zip.CreateEntryFromFile(ddlPath, "SchemaBuild_DDL.sql", CompressionLevel.Optimal);
                    }

                    // Update the run HTML index (best-effort).
                    ctx.Engine.UpdateRunIndexHtml(ctx.RunDir);
                }
                catch
                {
                    // best effort; do not fail stage
                }

                // Validate each statement (parse-only) to catch ORA-00904, ORA-00910, etc.
                var validator = new OracleDdlValidator(ctx.OpenOra);
                var validation = await validator.ValidateBundleAsync(bundle, ct);

                foreach (var ve in validation.Errors)
                {
                    errors.Add(new StageError(Stage.ToString(), ve.Schema ?? "", ve.ObjectName ?? "", ve.ErrorType, ve.Message, ve.Details));

                    // Add findings for confidence score / report.
                    findings.Add(new PreflightFinding
                    {
                        Severity = FindingSeverity.High,
                        Code = ve.ErrorCode ?? "DDL_PARSE",
                        Title = $"DDL validation failed for {ve.ObjectType} {ve.Schema}.{ve.ObjectName}",
                        Description = ve.Message,
                        RecommendedAction = "Use the generated DDL artifact to fix the script, then re-run Stage 5."
                    });
                }

                // Store artifacts regardless (helps manual fixes).
                var ddlBytes = Encoding.UTF8.GetBytes(bundle.CombinedSql);
                await ctx.Engine._toolMig.PutArtifactAsync(ctx.OpenSql, ctx.Run.RunId, "SchemaBuild_DDL.sql", "text/plain", ddlBytes, "Generated DDL scripts", ct);

                var reportJson = JsonSerializer.SerializeToUtf8Bytes(validation, new JsonSerializerOptions { WriteIndented = true });
                await ctx.Engine._toolMig.PutArtifactAsync(ctx.OpenSql, ctx.Run.RunId, "SchemaBuild_DDLValidation.json", "application/json", reportJson, "DDL parse validation report", ct);

                if (errors.Count > 0)
                    throw new StageFailedException(Stage, errors);

                await ctx.ToolMigStageAsync(Stage, "Completed", "DDL validated", 0);
                ctx.AppendLog($"[{Stage}] Completed.");

                // Refresh the run index page after a successful stage.
                ctx.Engine.UpdateRunIndexHtml(ctx.RunDir);
            }
            catch (StageFailedException sfe)
            {
                await ctx.ToolMigStageAsync(Stage, "Failed", sfe.Message, sfe.Errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, Stage.ToString(), sfe.Errors);
                throw;
            }
            catch (OracleException oex)
            {
                var ui = OracleErrorCatalog.Map(oex);
                errors.Add(StageError.FromException(Stage, "", "", oex));
                await ctx.ToolMigStageAsync(Stage, "Failed", ui.Title, errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, Stage.ToString(), errors);
                throw;
            }
            catch (Exception ex)
            {
                var errs = errors.Count > 0 ? errors : new List<StageError> { StageError.FromException(Stage, "", "", ex) };
                await ctx.ToolMigStageAsync(Stage, "Failed", ex.Message, errs.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, Stage.ToString(), errs);
                ctx.Engine.UpdateRunIndexHtml(ctx.RunDir);
                throw;
            }
        }
    }

    /// <summary>
    /// Phase 2 – Schema Build (Stages 4-6)
    /// Stage 6: DDL Deployment
    /// </summary>
    private sealed class SchemaBuildDdlDeploymentRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DeploymentSkeleton;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(Stage.ToString()))
            {
                ctx.Engine.Raise(Stage, "Skipping: already completed in prior run.");
                ctx.AppendLog($"[{Stage}] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(Stage, "Deploying schema skeleton (idempotent DDL)...");
            ctx.AppendLog($"[{Stage}] Starting...");
            await ctx.ToolMigStageAsync(Stage, "InProgress", "Deploying DDL", 0);

            var errors = new List<StageError>();
            var completedObjects = ctx.Request.ResumeRunId.HasValue
                ? await ctx.Engine._toolMig.GetCompletedObjectsAsync(ctx.OpenSql, ctx.Run.RunId, Stage.ToString(), ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            static string ObjKey(string schema, string name, string type) => $"{schema}.{name}|{type}";
            bool IsCompleted(string schema, string name, string type)
                => completedObjects.Contains(ObjKey(schema, name, type)) || completedObjects.Contains($"{schema}.{name}");

            var executor = new OracleDdlExecutor(ctx.OpenOra);
            static bool IsNonBlockingOracleException(OracleException oex, string objectType)
            {
                // Non-blocking during Schema Build: indexes too wide, XML/LOB expression indexes, already-exists, invalid compilation
                // Also defer views/procs/triggers/packages that are not converted from T-SQL.
                if (objectType.Equals("VIEW", StringComparison.OrdinalIgnoreCase) ||
                    objectType.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase) ||
                    objectType.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase) ||
                    objectType.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) ||
                    objectType.Equals("PACKAGE", StringComparison.OrdinalIgnoreCase))
                {
                    return oex.Number is 933 or 942 or 923 or 905 or 907 or 24344;
                }

                if (objectType.Equals("INDEX", StringComparison.OrdinalIgnoreCase))
                {
                    return oex.Number is 1450 or 2327 or 1408 or 955 or 2260 or 2261 or 24344;
                }

                // Generic idempotent / already-exists outcomes
                return oex.Number is 955 or 1408 or 2260 or 2261 or 24344;
            }


            try
            {
                // Load validated DDL from artifacts (preferred). If missing, compose again.
                var art = await ctx.Engine._toolMig.GetArtifactAsync(ctx.OpenSql, ctx.Run.RunId, "SchemaBuild_DDL.sql", ct);
                string ddlCombined;
                if (art is not null)
                    ddlCombined = Encoding.UTF8.GetString(art.Blob);
                else
                    ddlCombined = (await new SchemaBuildDdlComposer(ctx.Engine).ComposeAsync(ctx, ct)).CombinedSql;

                // Execute statements in order; per-object tracking uses ToolMig.ObjectStatus.
                var bundle = SchemaBuildDdlBundle.ParseCombined(ddlCombined);

                foreach (var stmt in bundle.Statements)
                {
                    ct.ThrowIfCancellationRequested();

                    var schema = stmt.Schema ?? ctx.Request.TargetSchema;
                    var name = stmt.ObjectName ?? stmt.ObjectType;
                    var type = stmt.ObjectType ?? "DDL";

                    if (!string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(name) && IsCompleted(schema, name, type))
                        continue;

                    await ctx.ToolMigObjectAsync(Stage, schema, name, type, "InProgress", null, null);

                    try
                    {
                        await executor.ExecuteIdempotentAsync(stmt.Sql, ct);
                        await ctx.ToolMigObjectAsync(Stage, schema, name, type, "Completed", null, null);
                    }
                    catch (OracleException oex)
                    {
                        var ui = OracleErrorCatalog.Map(oex);

                        if (IsNonBlockingOracleException(oex, type))
                        {
                            await ctx.ToolMigObjectAsync(Stage, schema, name, type, "Skipped", ui.Title, ui.Description);
                            ctx.AppendLog($"[{Stage}][WARN] {type} {schema}.{name}: Skipped ({ui.Title}, ORA-{oex.Number})");
                            continue;
                        }

                        errors.Add(StageError.FromException(Stage, schema, name, oex));
                        await ctx.ToolMigObjectAsync(Stage, schema, name, type, "Failed", ui.Title, ui.Description);
                        ctx.AppendLog($"[{Stage}][ERROR] {type} {schema}.{name}: {ui.Title} ({oex.Number})");
                        if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(StageError.FromException(Stage, schema, name, ex));
                        await ctx.ToolMigObjectAsync(Stage, schema, name, type, "Failed", ex.GetType().Name, ex.Message);
                        ctx.AppendLog($"[{Stage}][ERROR] {type} {schema}.{name}: {ex.Message}");
                        if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                    }
                }

                // Post-check: ensure tables exist before data migration.
                // This prevents silent no-op deployments from flowing into ORA-00942 during bulk copy.
                var missing = await ctx.Engine.FindMissingTargetTablesAsync(ctx.OpenOra, ctx, ct);
                foreach (var m in missing)
                {
                    errors.Add(new StageError(Stage.ToString(), m.TargetSchema, m.Table, "MissingTargetTable",
                        $"Target table {m.TargetSchema}.{m.Table} was not found after schema deployment.",
                        "Check SchemaBuild_DDL.sql and Oracle privileges; ensure DDL executed and committed."));
                }

                if (errors.Count > 0)
                    throw new StageFailedException(Stage, errors);

                if (errors.Count > 0)
                    throw new StageFailedException(Stage, errors);

                await ctx.ToolMigStageAsync(Stage, "Completed", "DDL deployed", 0);
                ctx.AppendLog($"[{Stage}] Completed.");

                ctx.Engine.UpdateRunIndexHtml(ctx.RunDir);
            }
            catch (StageFailedException sfe)
            {
                await ctx.ToolMigStageAsync(Stage, "Failed", sfe.Message, sfe.Errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, Stage.ToString(), sfe.Errors);
                ctx.Engine.UpdateRunIndexHtml(ctx.RunDir);
                throw;
            }
            catch (Exception ex)
            {
                var errs = errors.Count > 0 ? errors : new List<StageError> { StageError.FromException(Stage, "", "", ex) };
                await ctx.ToolMigStageAsync(Stage, "Failed", ex.Message, errs.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, Stage.ToString(), errs);
                ctx.Engine.UpdateRunIndexHtml(ctx.RunDir);
                throw;
            }
        }
    }
}
