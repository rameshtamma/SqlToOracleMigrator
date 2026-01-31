using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using SqlToOracleMigrator.Core.Migration;
using SqlToOracleMigrator.Core.Validation;
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

            // Ensure we are operating inside the intended PDB. If the user accidentally selected the CDB/XE connection
            // during resume, every object lookup/DDL will fail with ORA-00942 because the tables exist only in the PDB.
            await EnsureInTargetPdbAsync(ctx, ct);


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
            // Deep validation (inventory + PK/invalid checks + optional row counts)
            ctx.Engine.Raise(MigrationStage.PostValidation, "Running post-migration inventory validation (objects/keys/invalid)...");
            ctx.AppendLog("[PostValidation] Running deep validation (objects/keys/invalid)...");
            try
            {
                var schemas = ctx.Tables.Select(t => t.Schema)
                    .Concat(ctx.Views.Select(v => v.Schema))
                    .Concat(ctx.Procedures.Select(p => p.Schema))
                    .Concat(ctx.Functions.Select(f => f.Schema))
                    .Concat(ctx.Triggers.Select(t => t.Schema))
                    .Concat(ctx.Synonyms.Select(s => s.Schema))
                    .Concat(ctx.UserDefinedTypes.Select(u => u.Schema))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (schemas.Count > 0)
                {
                    var validator = new PostMigrationValidator(ctx.Engine._logger);
                    var opts = new PostMigrationValidationOptions
                    {
                        IncludeRowCounts = true,
                        IncludeKeyAndInvalidChecks = true,
                        RowCountParallelism = Math.Clamp(ctx.Dop, 1, 8)
                    };

                    var report = await validator.ValidateAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ctx.OpenOra, schemas, opts, ct);
                    var reportPath = await PostMigrationValidator.SaveReportAsync(report, ctx.RunDir, ct);
                    ctx.AppendLog($"[PostValidation] Validation report saved: {Path.GetFileName(reportPath)}");

                    foreach (var issue in report.Issues)
                    {
                        if (issue.Severity != ValidationSeverity.Error) continue;
                        errors.Add(new StageError(
                            MigrationStage.PostValidation.ToString(),
                            issue.Schema,
                            issue.Name,
                            issue.Category,
                            issue.Message,
                            issue.Details));
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(StageError.FromException(MigrationStage.PostValidation, "", "PostMigrationValidator", ex));
                ctx.AppendLog($"[PostValidation][ERROR] Deep validation failed: {ex.Message}");
            }

            var status = errors.Count == 0 ? "Completed" : "Failed";
            await ctx.ToolMigStageAsync(MigrationStage.PostValidation, status,
                errors.Count == 0 ? "Post validation complete" : $"Post validation FAILED with {errors.Count} error(s)",
                errors.Count);

            if (errors.Count > 0)
            {
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.PostValidation.ToString(), errors);
                throw new StageFailedException(MigrationStage.PostValidation, errors);
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

                // If we are in CDB$ROOT (or a different PDB), try to switch containers (requires SYSDBA).
                var alter = $"ALTER SESSION SET CONTAINER = {OracleIdent.FormatSchema(expected)}";
                await using var alterCmd = new OracleCommand(alter, ctx.OpenOra);
                await alterCmd.ExecuteNonQueryAsync(ct);

                ctx.AppendLog($"[PostValidation] Switched Oracle container: {current} -> {expected}");
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

