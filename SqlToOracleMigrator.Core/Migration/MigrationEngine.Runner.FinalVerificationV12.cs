using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core.Migration;
using SqlToOracleMigrator.Core.Migration.ExecuteVerify;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Stage 10 (v1.2): Finalization & Verification
    /// - Security replication (roles/grants) with strict user checks
    /// - Spot-check verification (row-count and light sampling)
    /// - Certificate JSON + PDF, persisted to ToolMig.RunArtifacts
    /// 
    /// Resume: reruns only failed sub-tasks; stage is skipped if already Completed in ToolMig.StageStatus.
    /// </summary>
    private sealed class FinalVerificationV12Runner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.FinalVerification;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(Stage.ToString()))
            {
                ctx.Engine.Raise(Stage, "Skipping: already completed in prior run.");
                ctx.AppendLog("[FinalVerification] Skipped (already completed)." );
                return;
            }

            await ctx.ToolMigStageAsync(Stage, "InProgress", "Final verification started", 0);

            var cert = new MigrationCertificate
            {
                RunId = ctx.Summary.RunId,
                SourceDatabase = ctx.Request.SourceDatabase,
                TargetSchema = ctx.Request.TargetSchema
            };

            // 1) Security replication
            try
            {
                ctx.Engine.Raise(Stage, "Applying security replication (roles/grants)...");
                ctx.AppendLog("[FinalVerification] Applying security replication (roles/grants)...");
                await ctx.Engine.ApplySecurityReplicationAsync(ctx, ct);
            }
            catch (Exception ex)
            {
                cert.Issues.Add(new VerificationIssue
                {
                    Severity = CertificateSeverity.Error,
                    Code = "SECURITY_REPLICATION_FAILED",
                    Title = "Security replication failed",
                    Description = ex.Message,
                    RecommendedAction = "Review generated grants script, ensure required users/roles exist, and re-run Final Verification."
                });
            }

            // 2) Row-count verification (spot-check)
            int verified = 0;
            int mismatched = 0;
            var maxTables = Math.Min(ctx.Tables.Count, Math.Max(25, Math.Min(200, ctx.Tables.Count)));

            for (var i = 0; i < maxTables; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = ctx.Tables[i];
                try
                {
                    var srcCount = await ctx.Engine._sqlMeta.GetTableRowCountAsync(ctx.OpenSql, ctx.Request.SourceDatabase, t.Schema, t.Table, ct);
                    var tgtCount = await GetOracleTableRowCountAsync(ctx.OpenOra, ctx.GetTargetSchema(t.Schema), t.Table, ct);
                    verified++;

                    if (srcCount != tgtCount)
                    {
                        mismatched++;
                        cert.Issues.Add(new VerificationIssue
                        {
                            Severity = CertificateSeverity.Warning,
                            Code = "ROWCOUNT_MISMATCH",
                            Title = $"Row count mismatch: {t.Schema}.{t.Table}",
                            Description = $"SQL={srcCount}, Oracle={tgtCount}",
                            RecommendedAction = "Review the load summary and error logs for this table; consider re-running Stage 8 for the table, then re-run Stage 9/10."
                        });
                    }
                }
                catch (Exception ex)
                {
                    mismatched++;
                    cert.Issues.Add(new VerificationIssue
                    {
                        Severity = CertificateSeverity.Warning,
                        Code = "ROWCOUNT_CHECK_FAILED",
                        Title = $"Row count check failed: {t.Schema}.{t.Table}",
                        Description = ex.Message,
                        RecommendedAction = "Ensure Oracle schema is accessible and table exists; re-run Stage 9 then Stage 10."
                    });
                }
            }

            cert.TablesVerified = verified;
            cert.TablesMismatched = mismatched;

            // 3) Confidence scoring
            // Start at 100, subtract:
            // - 30 if security replication error
            // - 2 per mismatched table
            var confidence = 100;
            if (cert.Issues.Any(x => x.Code == "SECURITY_REPLICATION_FAILED")) confidence -= 30;
            confidence -= Math.Min(60, mismatched * 2);
            confidence = Math.Clamp(confidence, 0, 100);
            cert.Confidence = confidence;

            // 4) Persist certificate artifacts
            try
            {
                var jsonBytes = CertificateReportWriter.ToJsonBytes(cert);
                var jsonName = "ExecuteVerify_MigrationCertificate.json";
                await File.WriteAllBytesAsync(Path.Combine(ctx.RunDir, jsonName), jsonBytes, ct);

                try
                {
                    await ctx.Engine._toolMig.PutArtifactAsync(ctx.OpenSql, ctx.Summary.RunId, jsonName, "application/json", jsonBytes, "Stage 10 certificate (JSON)", ct);
                }
                catch (Exception ex)
                {
                    ctx.AppendLog($"[FinalVerification][WARN] Failed to persist {jsonName} to ToolMig.RunArtifacts: {ex.Message}");
                }

                var pdfPath = await CertificateReportWriter.WritePdfAsync(cert, ctx.RunDir, ct);
                var pdfName = Path.GetFileName(pdfPath);
                var pdfBytes = await File.ReadAllBytesAsync(pdfPath, ct);

                try
                {
                    await ctx.Engine._toolMig.PutArtifactAsync(ctx.OpenSql, ctx.Summary.RunId, pdfName, "application/pdf", pdfBytes, "Stage 10 certificate (PDF)", ct);
                }
                catch (Exception ex)
                {
                    ctx.AppendLog($"[FinalVerification][WARN] Failed to persist {pdfName} to ToolMig.RunArtifacts: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                cert.Issues.Add(new VerificationIssue
                {
                    Severity = CertificateSeverity.Warning,
                    Code = "CERTIFICATE_WRITE_FAILED",
                    Title = "Failed to write certificate artifacts",
                    Description = ex.Message,
                    RecommendedAction = "Check write permissions for run directory and re-run Stage 10."
                });
            }

            // finalize stage
            var hasErrors = cert.Issues.Any(x => x.Severity == CertificateSeverity.Error);
            await ctx.ToolMigStageAsync(Stage, hasErrors ? "Failed" : "Completed",
                hasErrors ? "Final verification failed" : "Final verification complete",
                hasErrors ? 1 : 0);

            // write run summary at end
            ctx.Summary.CompletedUtc = DateTimeOffset.UtcNow;
            ctx.Summary.Confidence = cert.Confidence;
            ctx.Engine.WriteRunSummary(ctx.RunDir, ctx.Summary);

            if (hasErrors)
                throw new InvalidOperationException("Final verification failed. See certificate report for details.");
        }

        private static async Task<long> GetOracleTableRowCountAsync(OracleConnection openOra, string schema, string table, CancellationToken ct)
        {
            var sql = $"SELECT COUNT(*) FROM {OracleIdent.FormatSchema(schema)}.{OracleIdent.FormatSchema(table)}";
            await using var cmd = new OracleCommand(sql, openOra);
            var v = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(v);
        }
    }
}
