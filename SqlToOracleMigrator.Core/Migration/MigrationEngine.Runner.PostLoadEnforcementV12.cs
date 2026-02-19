using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core.Migration;
using SqlToOracleMigrator.Core.Tracking;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Stage 9 (v1.2): Post-Load Enforcement
    /// - Convert staged spatial/XML columns into final types (FROM_WKBGEOMETRY / XMLTYPE)
    /// - Deploy PK/UK + Indexes (post-conversion)
    /// - Deploy Foreign Keys (optionally NOVALIDATE then VALIDATE)
    /// - Gather schema stats (best effort)
    /// - Produce enforcement artifact reports
    /// 
    /// Resume: enforce only Pending/Error objects; skip Completed (ToolMig.ObjectStatus)
    /// </summary>
    private sealed class PostLoadEnforcementV12Runner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.PostLoadEnforcement;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(MigrationStage.PostLoadEnforcement.ToString()))
            {
                ctx.Engine.Raise(Stage, "Skipping: already completed in prior run.");
                ctx.AppendLog("[PostLoadEnforcement] Skipped (already completed).");
                return;
            }

            // Ensure correct PDB (avoid ORA-00942 when user connected to CDB root by mistake).
            await EnsureInTargetPdbAsync(ctx, ct);

            var errors = new List<StageError>();
            await ctx.ToolMigStageAsync(Stage, "InProgress", "Post-load enforcement started", 0);

            // 1) Convert special types (Spatial/XML) using staging columns.
            if (ctx.Request.RunStage9ConversionBeforeConstraintsAndIndexes)
            {
                ctx.Engine.Raise(Stage, "Converting staged spatial/XML before enforcement...");
                ctx.AppendLog("[PostLoadEnforcement] Converting staged spatial/XML before enforcement...");
                errors.AddRange(await ctx.Engine.ConvertSpatialAndXmlAsync(ctx, ct));
            }

            // 2) Deploy PK/UQ + indexes after conversion.
            ctx.Engine.Raise(Stage, "Deploying primary/unique constraints and indexes (post-conversion)...");
            ctx.AppendLog("[PostLoadEnforcement] Deploying primary/unique constraints and indexes (post-conversion)...");
            errors.AddRange(await ctx.Engine.DeployPrimaryKeysUniquesAndIndexesAsync(ctx, ct));

            // 3) Deploy foreign keys (after data load) to avoid load failures.
            if (ctx.Request.CreateForeignKeys && ctx.ForeignKeys.Count > 0)
            {
                ctx.Engine.Raise(Stage, $"Deploying foreign keys ({ctx.ForeignKeys.Count})...");
                ctx.AppendLog($"[PostLoadEnforcement] Deploying foreign keys ({ctx.ForeignKeys.Count})...");
                try
                {
                    var fkErrors = await ctx.Engine.DeployForeignKeysAsync(
                        ctx.OpenOra,
                        ctx.ForeignKeys,
                        ctx.GetTargetSchema,
                        ctx.Request.ForeignKeysEnableNoValidate,
                        ctx.Request.UseUnquotedUppercaseIdentifiers,
                        ct);
                    errors.AddRange(fkErrors);
                }
                catch (Exception ex)
                {
                    errors.Add(StageError.FromException(Stage, "", "ForeignKeys", ex));
                    ctx.AppendLog($"[PostLoadEnforcement][ERROR] Foreign key deployment failed: {ex.Message}");
                    if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                }
            }

            // 4) Gather stats (best effort)
            await ctx.Engine.GatherSchemaStatsAsync(ctx, ct);

            // 5) Write a small stage report artifact (JSON) + stage report file.
            try
            {
                var report = new
                {
                    Stage = Stage.ToString(),
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Errors = errors
                };

                var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var name = "ExecuteVerify_PostLoadEnforcement.json";
                var path = Path.Combine(ctx.RunDir, name);
                await File.WriteAllTextAsync(path, json, ct);

                try
                {
                    await ctx.Engine._toolMig.PutArtifactAsync(
                        ctx.OpenSql,
                        ctx.Summary.RunId,
                        name,
                        "application/json",
                        System.Text.Encoding.UTF8.GetBytes(json),
                        "Stage 9 enforcement report",
                        ct);
                }
                catch (Exception ex)
                {
                    ctx.AppendLog($"[PostLoadEnforcement][WARN] Failed to persist artifact {name} to ToolMig.RunArtifacts: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                // Non-fatal; enforcement already done.
                ctx.AppendLog($"[PostLoadEnforcement][WARN] Failed to write enforcement report: {ex.Message}");
            }

            var status = errors.Count == 0 ? "Completed" : "Failed";
            await ctx.ToolMigStageAsync(Stage, status,
                errors.Count == 0 ? "Post-load enforcement complete" : $"Post-load enforcement FAILED with {errors.Count} error(s)",
                errors.Count);

            if (errors.Count > 0)
            {
                ctx.Engine.WriteStageReport(ctx.RunDir, Stage.ToString(), errors);
                throw new StageFailedException(Stage, errors);
            }
        }

        private static async Task EnsureInTargetPdbAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (!ctx.Request.EnsureTargetPdb) return;

            var expected = (ctx.Request.TargetPdbName ?? string.Empty).Trim();
            if (expected.Length == 0) return;

            try
            {
                await using var cmd = new OracleCommand("SELECT SYS_CONTEXT('USERENV','CON_NAME') FROM dual", ctx.OpenOra);
                var v = await cmd.ExecuteScalarAsync(ct);
                var current = Convert.ToString(v) ?? string.Empty;

                if (current.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return;

                var alter = $"ALTER SESSION SET CONTAINER = {OracleIdent.FormatSchema(expected)}";
                await using var alterCmd = new OracleCommand(alter, ctx.OpenOra);
                await alterCmd.ExecuteNonQueryAsync(ct);

                ctx.AppendLog($"[PostLoadEnforcement] Switched Oracle container: {current} -> {expected}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Oracle connection is not in the expected PDB '{expected}'. " +
                    "Please select the PDB connection (service name) for validation/migration, or connect as SYS/SYSDBA so the tool can switch containers automatically.",
                    ex);
            }
        }
    }
}
