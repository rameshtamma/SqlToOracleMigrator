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
    private sealed class SchemaProvisioningRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.SchemaProvisioning;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(MigrationStage.SchemaProvisioning.ToString()))
            {
                ctx.Engine.Raise(MigrationStage.SchemaProvisioning, "Skipping: already completed in prior run.");
                ctx.AppendLog("[SchemaProvisioning] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(MigrationStage.SchemaProvisioning, "Provisioning target schemas/users...");
            ctx.AppendLog("[SchemaProvisioning] Starting...");
            await ctx.ToolMigStageAsync(MigrationStage.SchemaProvisioning, "InProgress", "Provisioning schemas/users", 0);

            var errors = new List<StageError>();

            try
            {
                if (ctx.Request.CloneSourceSchemas)
                {
                    var sourceSchemas = ctx.Tables.Select(t => t.Schema)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Option #2: reset target schemas on NEW runs (not resume)
                    if (!ctx.Request.ResumeRunId.HasValue)
                    {
                        var targetSchemasToReset = sourceSchemas
                            .Select(ctx.GetTargetSchema)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        foreach (var ts in targetSchemasToReset)
                        {
                            ct.ThrowIfCancellationRequested();
                            ctx.Engine.Raise(MigrationStage.SchemaProvisioning, $"Resetting target schema '{ts}' (dropping existing objects) ...");
                            ctx.AppendLog($"[SchemaProvisioning] Resetting target schema '{ts}' (dropping existing objects) ...");
                            await ResetTargetSchemaObjectsAsync(ctx.OpenOra, ts, ct);
                            await ctx.ToolMigObjectAsync(MigrationStage.SchemaProvisioning, ts, ts, "SCHEMA_RESET", "Completed", null, null);
                        }
                    }

                    foreach (var s in sourceSchemas)
                    {
                        ct.ThrowIfCancellationRequested();
                        var targetSchema = ctx.GetTargetSchema(s);
                        await ctx.ToolMigObjectAsync(MigrationStage.SchemaProvisioning, s, s, "SCHEMA", "InProgress", null, null);

                        try
                        {
                            await ctx.Engine._oraMeta.EnsureSchemaUserExistsAsync(ctx.OpenOra, targetSchema, ctx.Request.AutoCreateTargetSchemas, ct);
                            await ctx.ToolMigObjectAsync(MigrationStage.SchemaProvisioning, s, s, "SCHEMA", "Completed", null, null);
                        }
                        catch (Exception ex)
                        {
                            errors.Add(StageError.FromException(MigrationStage.SchemaProvisioning, s, s, ex));
                            await ctx.ToolMigObjectAsync(MigrationStage.SchemaProvisioning, s, s, "SCHEMA", "Failed", ex.GetType().Name, ex.Message);
                            ctx.AppendLog($"[SchemaProvisioning][ERROR] {s}: {ex.Message}");
                            if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                        }
                    }
                }
                else
                {
                    OracleMetadataProvider.ValidateOracleIdentifier(ctx.Request.TargetSchema);
                    var normalizedTarget = OracleIdent.FormatSchema(ctx.Request.TargetSchema);
                    await ctx.Engine._oraMeta.EnsureSchemaUserExistsAsync(ctx.OpenOra, normalizedTarget, ctx.Request.AutoCreateTargetSchemas, ct);
                }

                if (errors.Count > 0)
                    throw new StageFailedException(MigrationStage.SchemaProvisioning, errors);

                await ctx.ToolMigStageAsync(MigrationStage.SchemaProvisioning, "Completed", "Schemas/users ready", 0);
                ctx.AppendLog("[SchemaProvisioning] Completed.");
            }
            catch (StageFailedException sfe)
            {
                await ctx.ToolMigStageAsync(MigrationStage.SchemaProvisioning, "Failed", sfe.Message, sfe.Errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, sfe.Stage.ToString(), sfe.Errors);
                throw;
            }
            catch (Exception ex)
            {
                var errs = errors.Count > 0 ? errors : new List<StageError> { StageError.FromException(MigrationStage.SchemaProvisioning, "", "", ex) };
                await ctx.ToolMigStageAsync(MigrationStage.SchemaProvisioning, "Failed", ex.Message, errs.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.SchemaProvisioning.ToString(), errs);
                throw;
            }
        }
    }

    // Add this private static method to the MigrationEngine class to resolve CS0103
    private static async Task ResetTargetSchemaObjectsAsync(OracleConnection openOra, string targetSchema, CancellationToken ct)
    {
        // Drop all objects in the target schema using PL/SQL
        var plsql = BuildDropAllObjectsPlSql_AllObjectsOwnerQualified();
        using var cmd = openOra.CreateCommand();
        cmd.CommandText = plsql;
        cmd.BindByName = true;
        cmd.CommandType = System.Data.CommandType.Text;
        cmd.Parameters.Add("p_owner", OracleDbType.Varchar2).Value = (targetSchema ?? string.Empty).Trim().Trim('"').ToUpperInvariant();
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
