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
    private sealed class PostValidationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.PostValidation;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(MigrationStage.PostValidation.ToString()))
            {
                ctx.Engine.Raise(MigrationStage.PostValidation, "Skipping: already completed in prior run.");
                ctx.AppendLog("[PostValidation] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(MigrationStage.PostValidation, "Running basic row-count validation (first 50 tables)...");
            ctx.AppendLog("[PostValidation] Starting row-count validation (first 50 tables)...");
            await ctx.ToolMigStageAsync(MigrationStage.PostValidation, "InProgress", "Row-count validation", 0);

            var errors = new List<StageError>();
            foreach (var t in ctx.Tables.Take(50))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var srcCount = await ctx.Engine._sqlMeta.GetTableRowCountAsync(ctx.OpenSql, ctx.Request.SourceDatabase, t.Schema, t.Table, ct);
                    var tgtCount = await GetOracleTableRowCountAsync(ctx.OpenOra, ctx.GetTargetSchema(t.Schema), t.Table, ct);

                    if (srcCount != tgtCount)
                    {
                        var msg = $"Row count mismatch for {t.Schema}.{t.Table}: SQL={srcCount}, Oracle={tgtCount}";
                        ctx.Engine._logger.Warn(msg);
                        ctx.AppendLog($"[PostValidation][WARN] {msg}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(StageError.FromException(MigrationStage.PostValidation, t.Schema, t.Table, ex));
                    ctx.Engine._logger.Warn($"Validation failed for {t.Schema}.{t.Table}: {ex.Message}");
                    ctx.AppendLog($"[PostValidation][WARN] {t.Schema}.{t.Table}: {ex.Message}");
                    if (ctx.StageMode == ErrorHandlingMode.FailFast) break;
                }
            }



            // Deploy foreign keys AFTER data migration to avoid load failures.
            if (ctx.Request.CreateForeignKeys && ctx.ForeignKeys.Count > 0)
            {
                ctx.Engine.Raise(MigrationStage.PostValidation, $"Deploying foreign keys ({ctx.ForeignKeys.Count})...");
                ctx.AppendLog($"[PostValidation] Deploying foreign keys ({ctx.ForeignKeys.Count})...");
                try
                {
                    var fkErrors = await ctx.Engine.DeployForeignKeysAsync(ctx.OpenOra, ctx.ForeignKeys, ctx.GetTargetSchema, ctx.Request.ForeignKeysEnableNoValidate, ct);
                    errors.AddRange(fkErrors);
                }
                catch (Exception ex)
                {
                    errors.Add(StageError.FromException(MigrationStage.PostValidation, "", "ForeignKeys", ex));
                    ctx.AppendLog($"[PostValidation][ERROR] Foreign key deployment failed: {ex.Message}");
                    if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                }
            }
            await ctx.ToolMigStageAsync(MigrationStage.PostValidation, "Completed",
                errors.Count == 0 ? "Post validation complete" : $"Post validation complete with {errors.Count} warning(s)",
                errors.Count);

            if (errors.Count > 0)
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.PostValidation.ToString(), errors);
        }
    }
}
