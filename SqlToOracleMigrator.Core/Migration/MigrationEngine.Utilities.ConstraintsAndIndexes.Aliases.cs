using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// PostValidation in some branches calls DeployPrimaryKeysUniquesAndIndexesAsync(ctx, ct).
    /// Provide a compatible overload that routes to the full-context deploy for each table.
    /// </summary>
    private async Task<List<StageError>> DeployPrimaryKeysUniquesAndIndexesAsync(MigrationContext ctx, CancellationToken ct)
    {
        var errors = new List<StageError>();
        foreach (var t in ctx.Tables)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeployConstraintsAndIndexesAsync(
                    ctx.OpenSql,
                    ctx.OpenOra,
                    ctx.Request.SourceDatabase,
                    t.Schema,
                    t.Table,
                    ctx.GetTargetSchema(t.Schema),
                    ct);
            }
            catch (Exception ex)
            {
                errors.Add(StageError.FromException(MigrationStage.PostValidation, t.Schema, t.Table, ex));
            }
        }
        return errors;
    }

    /// <summary>
    /// Back-compat alias: older code calls with only (openOra, targetSchema). This cannot deploy per-table indexes,
    /// so we log and no-op. Prefer DeployPrimaryKeysUniquesAndIndexesAsync(ctx, ct) or the full-context overload.
    /// </summary>
    private Task DeployPrimaryKeysUniquesAndIndexesAsync(OracleConnection openOra, string targetSchema)
    {
        _logger?.Warn("DeployPrimaryKeysUniquesAndIndexesAsync(openOra, targetSchema) called without table/db context. No PK/UQ/index deployment performed. Update caller to pass MigrationContext or full context.");
        return Task.CompletedTask;
    }

    private Task DeployPrimaryKeysUniquesAndIndexesAsync(OracleConnection openOra, string targetSchema, CancellationToken ct)
        => DeployPrimaryKeysUniquesAndIndexesAsync(openOra, targetSchema);

    /// <summary>
    /// Preferred back-compat alias when full context is available.
    /// </summary>
    private Task DeployPrimaryKeysUniquesAndIndexesAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string sourceSchema,
        string table,
        string targetSchema,
        CancellationToken ct)
        => DeployConstraintsAndIndexesAsync(openSql, openOra, dbName, sourceSchema, table, targetSchema, ct);
}
