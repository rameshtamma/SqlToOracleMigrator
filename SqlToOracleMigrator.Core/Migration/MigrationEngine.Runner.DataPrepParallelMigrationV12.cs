using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core.Migration.DataPrep;
using SqlToOracleMigrator.Core.Oracle;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Stage 8 (v1.2): Parallel Data Migration
    /// - Uses OracleBulkCopy with BatchSize=50k
    /// - Fallback to stage-aware inserts on ORA-44003
    /// - Resume: skip Success; retry Pending/Error
    /// - Uses staging columns for XML/spatial (created in Stage 7)
    /// </summary>
    private sealed class DataPrepParallelDataMigrationV12Runner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.ParallelDataMigration;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(MigrationStage.ParallelDataMigration.ToString()))
            {
                ctx.Engine.Raise(MigrationStage.ParallelDataMigration, "Skipping: already completed in prior run.");
                ctx.AppendLog("[ParallelDataMigration] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(MigrationStage.ParallelDataMigration, $"Migrating table data in parallel (DOP={ctx.Dop}, BatchSize=50k)...");
            ctx.AppendLog($"[ParallelDataMigration] Starting (DOP={ctx.Dop}, BatchSize=50k)...");
            await ctx.ToolMigStageAsync(MigrationStage.ParallelDataMigration, "InProgress", $"Parallel data migration (DOP={ctx.Dop})", 0);

            var completedObjects = ctx.Request.ResumeRunId.HasValue
                ? await ctx.Engine._toolMig.GetCompletedObjectsAsync(ctx.OpenSql, ctx.Run.RunId, MigrationStage.ParallelDataMigration.ToString(), ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Load strategy (best-effort). If missing, proceed with defaults.
            var strategy = await TryLoadStrategyAsync(ctx, ct) ?? new DataPrepStrategy { SourceDatabase = ctx.Request.SourceDatabase };
            var stratByKey = strategy.Tables.ToDictionary(t => $"{t.Schema}.{t.Table}", t => t, StringComparer.OrdinalIgnoreCase);

            var toMigrate = ctx.Tables.Where(t => !completedObjects.Contains($"{t.Schema}.{t.Table}")).ToList();

            var errors = new ConcurrentBag<StageError>();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = linkedCts.Token;
            var semaphore = new SemaphoreSlim(ctx.Dop, ctx.Dop);
            var tasks = new List<Task>();

            int doneCount = 0;
            foreach (var t in toMigrate)
            {
                await semaphore.WaitAsync(token);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        await ctx.ToolMigObjectAsync(MigrationStage.ParallelDataMigration, t.Schema, t.Table, "TABLE", "InProgress", null, null);

                        var key = $"{t.Schema}.{t.Table}";
                        stratByKey.TryGetValue(key, out var tableStrat);

                        // Force BatchSize 50k per requirement; allow override per table.
                        var priorAccessor = ctx.Engine._requestAccessor;
                        var useBulk = tableStrat?.UseBulkCopy ?? true;
                        var batchSize = tableStrat?.BatchSizeOverride ?? 50000;
                        ctx.Engine.SetRequestAccessor(() => CloneRequestWithBulkOverrides(ctx.Request, useBulk, batchSize, bulkCopyUseInternalTransaction: true));

                        try
                        {
                            if (useBulk)
                            {
                                await ctx.Engine.CopyTableBulkAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, t.Schema, t.Table, ctx.GetTargetSchema(t.Schema), token);
                            }
                            else
                            {
                                await ctx.Engine.CopyTableAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, t.Schema, t.Table, ctx.GetTargetSchema(t.Schema), token);
                            }
                        }
                        finally
                        {
                            // Restore accessor
                            if (priorAccessor is not null)
                                ctx.Engine.SetRequestAccessor(priorAccessor);
                        }

                        await ctx.ToolMigObjectAsync(MigrationStage.ParallelDataMigration, t.Schema, t.Table, "TABLE", "Completed", null, null);
                    }
                    catch (OracleException oex)
                    {
                        errors.Add(StageError.FromException(MigrationStage.ParallelDataMigration, t.Schema, t.Table, oex));
                        await ctx.ToolMigObjectAsync(MigrationStage.ParallelDataMigration, t.Schema, t.Table, "TABLE", "Failed", "OracleException", oex.Message);
                        ctx.Engine._logger.Error($"[ParallelDataMigration] Oracle error for {t.Schema}.{t.Table}", oex);
                        ctx.AppendLog($"[ParallelDataMigration][ORA-{oex.Number}] {t.Schema}.{t.Table}: {oex.Message}");

                        // Fail-fast if configured
                        if (ctx.StageMode == ErrorHandlingMode.FailFast)
                        {
                            try { linkedCts.Cancel(); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(StageError.FromException(MigrationStage.ParallelDataMigration, t.Schema, t.Table, ex));
                        await ctx.ToolMigObjectAsync(MigrationStage.ParallelDataMigration, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                        ctx.Engine._logger.Error($"[ParallelDataMigration] Failed for {t.Schema}.{t.Table}", ex);
                        ctx.AppendLog($"[ParallelDataMigration][ERROR] {t.Schema}.{t.Table}: {ex.Message}");

                        if (ctx.StageMode == ErrorHandlingMode.FailFast)
                        {
                            try { linkedCts.Cancel(); } catch { }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                        var done = Interlocked.Increment(ref doneCount);
                        var pct = toMigrate.Count == 0 ? 1.0 : (double)done / toMigrate.Count;
                        ctx.Engine.Raise(MigrationStage.ParallelDataMigration, $"Completed {done}/{toMigrate.Count} tables.", pct);
                    }
                }, token));
            }

            try { await Task.WhenAll(tasks); } catch { }

            if (!errors.IsEmpty)
            {
                var list = errors.ToList();
                await ctx.ToolMigStageAsync(MigrationStage.ParallelDataMigration, "Failed", $"Parallel data migration failed with {list.Count} error(s)", list.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.ParallelDataMigration.ToString(), list);
                throw new StageFailedException(MigrationStage.ParallelDataMigration, list);
            }

            await ctx.ToolMigStageAsync(MigrationStage.ParallelDataMigration, "Completed", "Data loaded", 0);
            ctx.AppendLog("[ParallelDataMigration] Completed.");

            // Persist a stage summary artifact.
            await PersistSummaryAsync(ctx, strategy, ct);
        }

        private static async Task PersistSummaryAsync(MigrationContext ctx, DataPrepStrategy strategy, CancellationToken ct)
        {
            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    RunId = ctx.Run.RunId,
                    Stage = "ParallelDataMigration",
                    CompletedUtc = DateTimeOffset.UtcNow,
                    TableCount = ctx.Tables.Count,
                    UsedBulkCopyCount = strategy.Tables.Count(t => t.UseBulkCopy),
                    UsedFallbackCount = strategy.Tables.Count(t => !t.UseBulkCopy),
                    BatchSize = 50000
                }, new JsonSerializerOptions { WriteIndented = true });

                await ctx.Engine._toolMig.PutArtifactAsync(ctx.OpenSql, ctx.Run.RunId, "DataPrep_LoadSummary.json", "application/json", Encoding.UTF8.GetBytes(json), "Stage 8 load summary", ct);
                await File.WriteAllTextAsync(Path.Combine(ctx.RunDir, "DataPrep_LoadSummary.json"), json, ct);
            }
            catch (Exception ex)
            {
                ctx.AppendLog($"[ParallelDataMigration][WARN] Failed to persist LoadSummary artifact: {ex.Message}");
            }
        }

        private static async Task<DataPrepStrategy?> TryLoadStrategyAsync(MigrationContext ctx, CancellationToken ct)
        {
            try
            {
                // Prefer RunArtifacts (phase independence).
                var a = await ctx.Engine._toolMig.GetArtifactAsync(ctx.OpenSql, ctx.Run.RunId, "DataPrep_Strategy.json", ct);
                if (a?.Blob?.Length > 0)
                {
                    var json = Encoding.UTF8.GetString(a.Blob);
                    return JsonSerializer.Deserialize<DataPrepStrategy>(json);
                }
            }
            catch { }

            try
            {
                var p = Path.Combine(ctx.RunDir, "DataPrep_Strategy.json");
                if (File.Exists(p))
                {
                    var json = await File.ReadAllTextAsync(p, ct);
                    return JsonSerializer.Deserialize<DataPrepStrategy>(json);
                }
            }
            catch { }

            return null;
        }
    }
}
