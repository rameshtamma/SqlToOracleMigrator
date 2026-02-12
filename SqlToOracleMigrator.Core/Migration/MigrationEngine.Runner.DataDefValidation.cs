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
    private sealed class DataDefValidationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DataDefValidation;

        
        private static string SuppressSequenceDefaultsForValidation(string ddl)
        {
            if (string.IsNullOrWhiteSpace(ddl)) return ddl;

            // Oracle validates referenced sequences during parse; in a "parse-only" stage we may not have created sequences yet.
            // To keep this stage non-invasive (no CREATE SEQUENCE side-effects), we strip sequence-based DEFAULT clauses.
	            // Example: DEFAULT Sequences.CityID.NEXTVAL  -> <removed>
	            // NOTE: This pattern supports both unquoted and quoted identifiers.
	            // In a verbatim string literal, a quote is escaped by doubling it.
	            var pattern = @"\s+DEFAULT\s+(?:\w+|""[^""]+"")\.(?:\w+|""[^""]+"")\.NEXTVAL";
	            return System.Text.RegularExpressions.Regex.Replace(ddl, pattern, string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (!ctx.Request.EnableDataDefValidation)
                return;

            if (ctx.CompletedStages.Contains(MigrationStage.DataDefValidation.ToString()))
            {
                ctx.Engine.Raise(MigrationStage.DataDefValidation, "Skipping: already completed in prior run.");
                ctx.AppendLog("[ValidateDefinitions] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(MigrationStage.DataDefValidation, "Validating generated DDL (parse-only; no commit)...");
            ctx.AppendLog("[ValidateDefinitions] Starting...");
            await ctx.ToolMigStageAsync(MigrationStage.DataDefValidation, "InProgress", "Validating DDL (parse-only)", 0);

            var errors = new List<StageError>();
            var completedObjects = ctx.Request.ResumeRunId.HasValue
                ? await ctx.Engine._toolMig.GetCompletedObjectsAsync(ctx.OpenSql, ctx.Run.RunId, MigrationStage.DataDefValidation.ToString(), ct)
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

                    await ctx.ToolMigObjectAsync(MigrationStage.DataDefValidation, t.Schema, t.Table, "TABLE", "InProgress", null, null);
                    string? ddlForDiagnostics = null;
                    try
                    {
                        var columns = await ctx.Engine._sqlMeta.GetTableColumnsAsync(ctx.OpenSql, ctx.Request.SourceDatabase, t.Schema, t.Table, ct);
                        ddlForDiagnostics = OracleDdlGenerator.CreateTableDdl(ctx.GetTargetSchema(t.Schema), t.Table, columns, ctx.Engine._typeMapper);
                        ddlForDiagnostics = SuppressSequenceDefaultsForValidation(ddlForDiagnostics);
                        await ctx.Engine._oraMeta.ValidateDdlAsync(ctx.OpenOra, ddlForDiagnostics, ct);
                        await ctx.ToolMigObjectAsync(MigrationStage.DataDefValidation, t.Schema, t.Table, "TABLE", "Completed", null, null);
                    }
                    catch (Exception ex)
                    {
                        var details = (ddlForDiagnostics is null)
                            ? ex.ToString()
                            : $"Generated DDL:\n{ddlForDiagnostics}\n\nException:\n{ex}";
                        errors.Add(new StageError(MigrationStage.DataDefValidation.ToString(), t.Schema, t.Table, ex.GetType().Name, ex.Message, details));
                        await ctx.ToolMigObjectAsync(MigrationStage.DataDefValidation, t.Schema, t.Table, "TABLE", "Failed", ex.GetType().Name, ex.Message);
                        ctx.AppendLog($"[ValidateDefinitions][ERROR] {t.Schema}.{t.Table}: {ex.Message}");
                        if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                    }

                    if (ctx.Tables.Count > 0 && i % 50 == 0)
                        ctx.Engine.Raise(MigrationStage.DataDefValidation, $"Validated DDL for {i}/{ctx.Tables.Count} tables...", (double)i / ctx.Tables.Count);
                }

                if (errors.Count > 0)
                    throw new StageFailedException(MigrationStage.DataDefValidation, errors);

                await ctx.ToolMigStageAsync(MigrationStage.DataDefValidation, "Completed", "DDL validation passed", 0);
                ctx.AppendLog("[ValidateDefinitions] Completed with no issues.");
            }
            catch (StageFailedException sfe)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DataDefValidation, "Failed", sfe.Message, sfe.Errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, sfe.Stage.ToString(), sfe.Errors);
                throw;
            }
            catch (Exception ex)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DataDefValidation, "Failed", ex.Message, errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.DataDefValidation.ToString(), errors.Count > 0
                    ? errors
                    : new List<StageError> { StageError.FromException(MigrationStage.DataDefValidation, "", "", ex) });
                throw;
            }
        }
    }
}