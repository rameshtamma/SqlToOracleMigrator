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
    private sealed class DataMigrationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DataMigration;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(MigrationStage.DataMigration.ToString()))
            {
                ctx.Engine.Raise(MigrationStage.DataMigration, "Skipping: already completed in prior run.");
                ctx.AppendLog("[DataMigration] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(MigrationStage.DataMigration, $"Migrating table data in parallel (DOP={ctx.Dop})...");
            ctx.AppendLog($"[DataMigration] Starting (DOP={ctx.Dop})...");
            await ctx.ToolMigStageAsync(MigrationStage.DataMigration, "InProgress", $"Data migration (DOP={ctx.Dop})", 0);

            var completedObjects = ctx.Request.ResumeRunId.HasValue
                ? await ctx.Engine._toolMig.GetCompletedObjectsAsync(ctx.OpenSql, ctx.Run.RunId, MigrationStage.DataMigration.ToString(), ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var toMigrate = ctx.Tables
                .Where(t => !completedObjects.Contains($"{t.Schema}.{t.Table}"))
                .ToList();

            var errors = new ConcurrentBag<StageError>();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = linkedCts.Token;
            var semaphore = new SemaphoreSlim(ctx.Dop, ctx.Dop);
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
                        await ctx.ToolMigObjectAsync(MigrationStage.DataMigration, t.Schema, t.Table, "TABLE", "InProgress", null, null);
                        if (ctx.Request.UseOracleBulkCopy)
                        {
                            await ctx.Engine.CopyTableBulkAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, t.Schema, t.Table, ctx.GetTargetSchema(t.Schema), token);
                        }
                        else
                        {
                            await ctx.Engine.CopyTableAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, t.Schema, t.Table, ctx.GetTargetSchema(t.Schema), token);
                        }
                        await ctx.ToolMigObjectAsync(MigrationStage.DataMigration, t.Schema, t.Table, "TABLE", "Completed", null, null);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(StageError.FromException(MigrationStage.DataMigration, t.Schema, t.Table, ex));
                        await ctx.ToolMigObjectAsync(MigrationStage.DataMigration, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                        ctx.Engine._logger.Error($"Data migration failed for {t.Schema}.{t.Table}", ex);
                        ctx.AppendLog($"[DataMigration][ERROR] {t.Schema}.{t.Table}: {ex.Message}");

                        if (ctx.StageMode == ErrorHandlingMode.FailFast)
                        {
                            try { linkedCts.Cancel(); } catch { }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                        var done = Interlocked.Increment(ref completed);
                        var pct = toMigrate.Count == 0 ? 1.0 : (double)done / toMigrate.Count;
                        ctx.Engine.Raise(MigrationStage.DataMigration, $"Completed {done}/{toMigrate.Count} tables.", pct);
                    }
                }, token));
            }

            try { await Task.WhenAll(tasks); } catch { }

            if (!errors.IsEmpty)
            {
                var list = errors.ToList();
                await ctx.ToolMigStageAsync(MigrationStage.DataMigration, "Failed", $"Data migration failed with {list.Count} error(s)", list.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.DataMigration.ToString(), list);
                throw new StageFailedException(MigrationStage.DataMigration, list);
            }

            await ctx.ToolMigStageAsync(MigrationStage.DataMigration, "Completed", "Data migrated", 0);
            ctx.AppendLog("[DataMigration] Completed.");
        }
    }
}
