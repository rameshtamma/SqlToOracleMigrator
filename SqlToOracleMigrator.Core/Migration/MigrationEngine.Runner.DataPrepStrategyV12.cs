using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Core.Migration.DataPrep;
using SqlToOracleMigrator.Core.Oracle;
using System.Text;
using System.Text.Json;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Stage 7 (v1.2): Data Strategy & "Golden Row" Test
    /// - Sample TOP 100 rows per table
    /// - Detect NOT NULL violations / date parse risks
    /// - Identify XML/spatial columns and prepare staging columns
    /// - Output Strategy JSON + report and persist to ToolMig.RunArtifacts
    /// </summary>
    private sealed class DataPrepStrategySamplingV12Runner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DataStrategySampling;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(MigrationStage.DataStrategySampling.ToString()))
            {
                ctx.Engine.Raise(MigrationStage.DataStrategySampling, "Skipping: already completed in prior run.");
                ctx.AppendLog("[DataStrategy] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(MigrationStage.DataStrategySampling, "Building data strategy (Golden Row sampling)...");
            ctx.AppendLog("[DataStrategy] Starting (Golden Row sampling TOP 100)...");
            await ctx.ToolMigStageAsync(MigrationStage.DataStrategySampling, "InProgress", "Data strategy sampling", 0);

            var completedObjects = ctx.Request.ResumeRunId.HasValue
                ? await ctx.Engine._toolMig.GetCompletedObjectsAsync(ctx.OpenSql, ctx.Run.RunId, MigrationStage.DataStrategySampling.ToString(), ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var sampler = new GoldenRowSampler(ctx.Engine._sqlMeta);
            var builder = new DataStrategyBuilder(ctx.Engine._sqlMeta);
            var strategy = new DataPrepStrategy
            {
                SourceDatabase = ctx.Request.SourceDatabase,
                DefaultBatchSize = 50000,
                DefaultFallbackBatchRows = 1000
            };

            var report = new DataPrepReport { Strategy = strategy };

            // Sample in parallel with bounded concurrency.
            var errors = new List<StageError>();
            var findings = new List<DataPrepFinding>();

            var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(8, ctx.Dop)), Math.Max(1, Math.Min(8, ctx.Dop)));
            var tasks = new List<Task>();
            var lockObj = new object();

            foreach (var t in ctx.Tables)
            {
                var key = $"{t.Schema}.{t.Table}";
                if (completedObjects.Contains(key))
                    continue;

                await semaphore.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        await ctx.ToolMigObjectAsync(MigrationStage.DataStrategySampling, t.Schema, t.Table, "TABLE", "InProgress", null, null);

                        var sample = await sampler.SampleTopAsync(ctx.OpenSql, ctx.Request.SourceDatabase, t.Schema, t.Table, topN: 100, ct);
                        var tableStrat = await builder.BuildForTableAsync(ctx.OpenSql, ctx.Request.SourceDatabase, t.Schema, t.Table, sample, ct);

                        lock (lockObj)
                        {
                            strategy.Tables.Add(tableStrat);
                        }

                        // If staging required, ensure staging columns exist (idempotent) and relax NOT NULL where required.
                        if (tableStrat.RequiresXmlStaging || tableStrat.RequiresSpatialStaging)
                        {
                            var cols = await ctx.Engine._sqlMeta.GetTableColumnsAsync(ctx.OpenSql, ctx.Request.SourceDatabase, t.Schema, t.Table, ct);
                            var srcCols = cols.Select(c => (c.ColumnName, c.SqlTypeName, c.IsNullable)).ToList();
                            var prep = new OracleStagingPreparer(ctx.OpenOra);
                            await prep.EnsureStagingForTableAsync(ctx.GetTargetSchema(t.Schema), t.Table, srcCols, tableStrat.RelaxNotNullOnStagedColumns, ct);
                        }

                        // Findings
                        if (sample.NotNullViolations > 0)
                        {
                            lock (lockObj)
                            {
                                findings.Add(new DataPrepFinding
                                {
                                    Code = "NOT_NULL_SAMPLE_VIOLATION",
                                    Title = "NULLs found in NOT NULL columns (sample)",
                                    Severity = "High",
                                    Description = $"Sampled rows for {t.Schema}.{t.Table} contain NULL values in NOT NULL columns. Oracle load would fail with ORA-01400.",
                                    RecommendedAction = "Investigate source data quality. Tool will apply default policy for Stage 8 to prevent load failures."
                                });
                            }
                        }

                        if (sample.DateParseWarnings > 0)
                        {
                            lock (lockObj)
                            {
                                findings.Add(new DataPrepFinding
                                {
                                    Code = "DATE_SAMPLE_WARNING",
                                    Title = "Date conversion risk detected (sample)",
                                    Severity = "Medium",
                                    Description = $"Sampled rows for {t.Schema}.{t.Table} include date/time values that may not convert cleanly to Oracle (ORA-01843 risk).",
                                    RecommendedAction = "Consider normalizing date formats or clamping invalid values. The tool will use a safe default for NOT NULL date columns when source violates constraints."
                                });
                            }
                        }

                        await ctx.ToolMigObjectAsync(MigrationStage.DataStrategySampling, t.Schema, t.Table, "TABLE", "Completed", null, null);
                    }
                    catch (Exception ex)
                    {
                        lock (lockObj)
                        {
                            errors.Add(StageError.FromException(MigrationStage.DataStrategySampling, t.Schema, t.Table, ex));
                        }
                        await ctx.ToolMigObjectAsync(MigrationStage.DataStrategySampling, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                        ctx.Engine._logger.Error($"[DataStrategy] Failed for {t.Schema}.{t.Table}", ex);
                        ctx.AppendLog($"[DataStrategy][ERROR] {t.Schema}.{t.Table}: {ex.Message}");
                        if (ctx.StageMode == ErrorHandlingMode.FailFast)
                            throw;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct));
            }

            try { await Task.WhenAll(tasks); } catch { /* handled via errors */ }

            report.Findings = findings;
            report.Confidence = ComputeConfidenceFromFindings(findings);
            report.Risk = findings.Any(f => f.Severity.Equals("High", StringComparison.OrdinalIgnoreCase)) ? DataStrategyRisk.High
                        : findings.Any(f => f.Severity.Equals("Medium", StringComparison.OrdinalIgnoreCase)) ? DataStrategyRisk.Medium
                        : DataStrategyRisk.Low;

            // Persist artifacts
            var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
            var strategyJson = JsonSerializer.Serialize(strategy, jsonOpts);
            var reportJson = JsonSerializer.Serialize(report, jsonOpts);

            await PersistArtifactAsync(ctx, "DataPrep_Strategy.json", "application/json", Encoding.UTF8.GetBytes(strategyJson), "Stage 7 data strategy", ct);
            await PersistArtifactAsync(ctx, "DataPrep_GoldenRowReport.json", "application/json", Encoding.UTF8.GetBytes(reportJson), "Stage 7 Golden Row report", ct);

            // Also write to run folder.
            await File.WriteAllTextAsync(Path.Combine(ctx.RunDir, "DataPrep_Strategy.json"), strategyJson, ct);
            await File.WriteAllTextAsync(Path.Combine(ctx.RunDir, "DataPrep_GoldenRowReport.json"), reportJson, ct);

            // Stage-level completion.
            if (errors.Count > 0)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DataStrategySampling, "Failed", $"Data strategy failed with {errors.Count} error(s)", errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.DataStrategySampling.ToString(), errors);
                throw new StageFailedException(MigrationStage.DataStrategySampling, errors);
            }

            // Update phase confidence so Phase 3 report becomes meaningful.
            ctx.Summary.Confidence = report.Confidence;

            await ctx.ToolMigStageAsync(MigrationStage.DataStrategySampling, "Completed", $"Data strategy built (confidence {report.Confidence}%)", 0);
            ctx.AppendLog($"[DataStrategy] Completed. Confidence={report.Confidence}% Risk={report.Risk}.");
        }

        private static int ComputeConfidenceFromFindings(List<DataPrepFinding> findings)
        {
            var score = 100;
            foreach (var f in findings)
            {
                score -= f.Severity switch
                {
                    "High" => 20,
                    "Medium" => 10,
                    "Low" => 5,
                    _ => 0
                };
            }

            return Math.Clamp(score, 0, 100);
        }

        private static async Task PersistArtifactAsync(
            MigrationContext ctx,
            string name,
            string contentType,
            byte[] blob,
            string description,
            CancellationToken ct)
        {
            try
            {
                await ctx.Engine._toolMig.PutArtifactAsync(ctx.OpenSql, ctx.Run.RunId, name, contentType, blob, description, ct);
            }
            catch (Exception ex)
            {
                // Artifact persistence must not break migration.
                ctx.AppendLog($"[DataStrategy][WARN] Failed to persist artifact {name} to ToolMig.RunArtifacts: {ex.Message}");
            }
        }
    }
}
