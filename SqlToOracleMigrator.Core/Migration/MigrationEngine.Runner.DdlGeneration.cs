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
    private sealed class DdlGenerationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DdlGeneration;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(MigrationStage.DdlGeneration.ToString()))
            {
                ctx.Engine.Raise(MigrationStage.DdlGeneration, "Skipping: already completed in prior run.");
                ctx.AppendLog("[DdlGeneration] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(MigrationStage.DdlGeneration, "Generating and deploying table DDL...");
            ctx.AppendLog("[DdlGeneration] Starting...");
            await ctx.ToolMigStageAsync(MigrationStage.DdlGeneration, "InProgress", "Generating + deploying DDL", 0);

            var errors = new List<StageError>();
            var completedObjects = ctx.Request.ResumeRunId.HasValue
                ? await ctx.Engine._toolMig.GetCompletedObjectsAsync(ctx.OpenSql, ctx.Run.RunId, MigrationStage.DdlGeneration.ToString(), ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var t in ctx.Tables)
                {
                    ct.ThrowIfCancellationRequested();
                    var key = $"{t.Schema}.{t.Table}";
                    if (completedObjects.Contains(key))
                        continue;

                    await ctx.ToolMigObjectAsync(MigrationStage.DdlGeneration, t.Schema, t.Table, "TABLE", "InProgress", null, null);
                    try
                    {
                        await ctx.Engine.DeployTableAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, t.Schema, t.Table, ctx.GetTargetSchema(t.Schema), ct);
                        await ctx.ToolMigObjectAsync(MigrationStage.DdlGeneration, t.Schema, t.Table, "TABLE", "Completed", null, null);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(StageError.FromException(MigrationStage.DdlGeneration, t.Schema, t.Table, ex));
                        await ctx.ToolMigObjectAsync(MigrationStage.DdlGeneration, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                        ctx.AppendLog($"[DdlGeneration][ERROR] {t.Schema}.{t.Table}: {ex.Message}");
                        if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                    }
                }

                if (errors.Count > 0)
                    throw new StageFailedException(MigrationStage.DdlGeneration, errors);

                await ctx.ToolMigStageAsync(MigrationStage.DdlGeneration, "Completed", "DDL deployed", 0);
                ctx.AppendLog("[DdlGeneration] Completed.");
            }
            catch (StageFailedException sfe)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DdlGeneration, "Failed", sfe.Message, sfe.Errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, sfe.Stage.ToString(), sfe.Errors);
                throw;
            }
            catch (Exception ex)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DdlGeneration, "Failed", ex.Message, errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.DdlGeneration.ToString(), errors.Count > 0
                    ? errors
                    : new List<StageError> { StageError.FromException(MigrationStage.DdlGeneration, "", "", ex) });
                throw;
            }
        }
    }
}
