using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using SqlToOracleMigrator.Core.Migration;
using SqlToOracleMigrator.Core.Tracking;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Phase 2 Stage 4 (v1.2): Schema Provisioning & Smart Reset.
    /// This is separate from the legacy SchemaProvisioning stage to support phased UI and ToolMig.GroupStatus.
    /// </summary>
    private sealed class SchemaBuildProvisioningRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.Provisioning;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(Stage.ToString()))
            {
                ctx.Engine.Raise(Stage, "Skipping: already completed in prior run.");
                ctx.AppendLog($"[{Stage}] Skipped (already completed)." );
                return;
            }

            ctx.Engine.Raise(Stage, "Provisioning target schemas/users (smart reset)...");
            ctx.AppendLog($"[{Stage}] Starting...");
            await ctx.ToolMigStageAsync(Stage, "InProgress", "Provisioning schemas/users", 0);

            var errors = new List<StageError>();

            try
            {
                if (ctx.Request.CloneSourceSchemas)
                {
                    var sourceSchemas = ctx.Tables.Select(t => t.Schema)
                        .Concat(ctx.Views.Select(v => v.Schema))
                        .Concat(ctx.Procedures.Select(p => p.Schema))
                        .Concat(ctx.Functions.Select(f => f.Schema))
                        .Concat(ctx.Triggers.Select(tr => tr.Schema))
                        .Concat(ctx.Synonyms.Select(sy => sy.Schema))
                        .Concat(ctx.Sequences.Select(sq => sq.Schema))
                        .Concat(ctx.UserDefinedTypes.Select(udt => udt.Schema))
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (ctx.Request.OverrideTargetObjectsEachRun && !ctx.Request.ResumeRunId.HasValue)
                    {
                        var targetSchemasToReset = sourceSchemas.Select(ctx.GetTargetSchema)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        foreach (var ts in targetSchemasToReset)
                        {
                            ct.ThrowIfCancellationRequested();
                            await SmartResetAsync(ctx, ts, ct);
                            await ctx.ToolMigObjectAsync(Stage, ts, ts, "SCHEMA_RESET", "Completed", null, null);
                        }
                    }

                    foreach (var s in sourceSchemas)
                    {
                        ct.ThrowIfCancellationRequested();
                        var targetSchema = ctx.GetTargetSchema(s);
                        await ctx.ToolMigObjectAsync(Stage, s, s, "SCHEMA", "InProgress", null, null);
                        try
                        {
                            await ctx.Engine._oraMeta.EnsureSchemaUserExistsAsync(ctx.OpenOra, targetSchema, ctx.Request.AutoCreateTargetSchemas, ct);
                            await ctx.ToolMigObjectAsync(Stage, s, s, "SCHEMA", "Completed", null, null);
                        }
                        catch (Exception ex)
                        {
                            errors.Add(StageError.FromException(Stage, s, s, ex));
                            await ctx.ToolMigObjectAsync(Stage, s, s, "SCHEMA", "Failed", ex.GetType().Name, ex.Message);
                            if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                        }
                    }
                }
                else
                {
                    OracleMetadataProvider.ValidateOracleIdentifier(ctx.Request.TargetSchema);
                    var normalizedTarget = OracleIdent.FormatSchema(ctx.Request.TargetSchema);
                    await ctx.Engine._oraMeta.EnsureSchemaUserExistsAsync(ctx.OpenOra, normalizedTarget, ctx.Request.AutoCreateTargetSchemas, ct);

                    if (ctx.Request.OverrideTargetObjectsEachRun && !ctx.Request.ResumeRunId.HasValue)
                    {
                        ct.ThrowIfCancellationRequested();
                        await SmartResetAsync(ctx, normalizedTarget, ct);
                        await ctx.ToolMigObjectAsync(Stage, normalizedTarget, normalizedTarget, "SCHEMA_RESET", "Completed", null, null);
                    }
                }

                if (errors.Count > 0)
                    throw new StageFailedException(Stage, errors);

                await ctx.ToolMigStageAsync(Stage, "Completed", "Schemas/users ready", 0);
                ctx.AppendLog($"[{Stage}] Completed.");
            }
            catch (StageFailedException sfe)
            {
                await ctx.ToolMigStageAsync(Stage, "Failed", sfe.Message, sfe.Errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, sfe.Stage.ToString(), sfe.Errors);
                throw;
            }
            catch (Exception ex)
            {
                var errs = errors.Count > 0 ? errors : new List<StageError> { StageError.FromException(Stage, "", "", ex) };
                await ctx.ToolMigStageAsync(Stage, "Failed", ex.Message, errs.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, Stage.ToString(), errs);
                throw;
            }
        }

        private static async Task SmartResetAsync(MigrationContext ctx, string targetSchema, CancellationToken ct)
        {
            ctx.Engine.Raise(MigrationStage.Provisioning, $"Smart reset: {targetSchema}");
            ctx.AppendLog($"[{MigrationStage.Provisioning}] Smart reset: {targetSchema}");

            // Preferred: drop user if tool is allowed to auto-create schemas.
            if (ctx.Request.AutoCreateTargetSchemas)
            {
                try
                {
                    await ctx.Engine._oraMeta.DropUserIfExistsAsync(ctx.OpenOra, targetSchema, ct);
                }
                catch
                {
                    // If drop user fails (e.g., ORA-01940), fall back to object-level reset.
                    await ResetTargetSchemaObjectsAsync(ctx.OpenOra, targetSchema, ct);
                }

                await ctx.Engine._oraMeta.EnsureSchemaUserExistsAsync(ctx.OpenOra, targetSchema, true, ct);
            }
            else
            {
                await ResetTargetSchemaObjectsAsync(ctx.OpenOra, targetSchema, ct);
            }

            await ctx.Engine._oraMeta.PurgeRecycleBinAsync(ctx.OpenOra, ct);
        }
    }
}
