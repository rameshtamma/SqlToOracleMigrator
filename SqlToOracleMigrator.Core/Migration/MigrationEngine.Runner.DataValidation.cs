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
    private sealed class DataValidationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DataValidation;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (!ctx.Request.EnableDataValidation)
                return;

            if (ctx.CompletedStages.Contains(MigrationStage.DataValidation.ToString()))
            {
                ctx.Engine.Raise(MigrationStage.DataValidation, "Skipping: already completed in prior run.");
                ctx.AppendLog("[ValidateData] Skipped (already completed).");
                return;
            }

            var limit = ctx.Request.ValidateFullDataset ? int.MaxValue : Math.Max(0, ctx.Request.DataValidationRowLimit);
            if (limit <= 0 && !ctx.Request.ValidateFullDataset)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DataValidation, "Skipped", "Row limit <= 0", 0);
                ctx.Engine._logger.Info("[DataValidation] Skipped (row limit <= 0).");
                ctx.AppendLog("[ValidateData] Skipped (row limit <= 0).");
                return;
            }

            ctx.Engine.Raise(MigrationStage.DataValidation, ctx.Request.ValidateFullDataset
                ? "Validating data migration (full dataset; rollback)..."
                : $"Validating data migration (top {limit:N0} rows/table; rollback)...");

            ctx.AppendLog(ctx.Request.ValidateFullDataset
                ? "[ValidateData] Starting (FULL dataset; rollback)..."
                : $"[ValidateData] Starting (TOP {limit:N0} rows/table; rollback)...");

            await ctx.ToolMigStageAsync(MigrationStage.DataValidation, "InProgress", "Validating data (dry-run)", 0);

            var errors = new List<StageError>();
            var completedObjects = ctx.Request.ResumeRunId.HasValue
                ? await ctx.Engine._toolMig.GetCompletedObjectsAsync(ctx.OpenSql, ctx.Run.RunId, MigrationStage.DataValidation.ToString(), ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var i = 0;
                foreach (var t in ctx.Tables)
                {
                    ct.ThrowIfCancellationRequested();
                    i++;
                    var key = $"{t.Schema}.{t.Table}";
                    if (completedObjects.Contains(key))
                        continue;

                    await ctx.ToolMigObjectAsync(MigrationStage.DataValidation, t.Schema, t.Table, "TABLE", "InProgress", null, null);
                    try
                    {
                        await ctx.Engine.ValidateTableDataAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, t.Schema, t.Table, ctx.GetTargetSchema(t.Schema), ctx.Request.ValidateFullDataset, limit, ct);
                        await ctx.ToolMigObjectAsync(MigrationStage.DataValidation, t.Schema, t.Table, "TABLE", "Completed", null, null);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(StageError.FromException(MigrationStage.DataValidation, t.Schema, t.Table, ex));
                        await ctx.ToolMigObjectAsync(MigrationStage.DataValidation, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                        ctx.AppendLog($"[ValidateData][ERROR] {t.Schema}.{t.Table}: {ex.Message}");
                        if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                    }

                    if (ctx.Tables.Count > 0 && i % 25 == 0)
                        ctx.Engine.Raise(MigrationStage.DataValidation, $"Validated data for {i}/{ctx.Tables.Count} tables...", (double)i / ctx.Tables.Count);
                }

                if (errors.Count > 0)
                    throw new StageFailedException(MigrationStage.DataValidation, errors);

                await ctx.ToolMigStageAsync(MigrationStage.DataValidation, "Completed", "Data validation passed", 0);
                ctx.AppendLog("[ValidateData] Completed with no issues.");
            }
            catch (StageFailedException sfe)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DataValidation, "Failed", sfe.Message, sfe.Errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, sfe.Stage.ToString(), sfe.Errors);
                throw;
            }
            catch (Exception ex)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DataValidation, "Failed", ex.Message, errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.DataValidation.ToString(), errors.Count > 0
                    ? errors
                    : new List<StageError> { StageError.FromException(MigrationStage.DataValidation, "", "", ex) });
                throw;
            }
        }
    }
}
