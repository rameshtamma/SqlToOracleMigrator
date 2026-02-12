using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private async Task CopyTableBulkAsync(SqlConnection openSql, OracleConnection openOra, string dbName, string schema, string table, string targetSchema, CancellationToken ct)
    {
        var columns = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, ct);
        if (columns.Count == 0) return;

        // Build projection with safe casts and staging columns for XML/spatial where needed.
        var selectSql = BuildSqlSelectForTable(schema, table, columns, useTopN: false);

        await using var selectCmd = new SqlCommand(selectSql, openSql) { CommandTimeout = 0 };
        await using var reader = await selectCmd.ExecuteReaderAsync(ct);

        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var destTable = $"{schemaPrefix}.{OracleIdent.QuoteIdent(table)}";

        var opts = ctxBulkCopyOptions();
        using var bulk = new OracleBulkCopy(openOra, opts)
        {
            DestinationTableName = destTable,
            BatchSize = Math.Max(1, ctBatchSize()),
            BulkCopyTimeout = Math.Max(0, ctBulkTimeout())
        };

        // Guard: ensure the reader column count matches the target table column count.
        var targetColCount = await GetOracleTargetColumnCountAsync(openOra, targetSchema, table, ct);
        if (targetColCount > 0 && targetColCount != reader.FieldCount)
        {
            throw new InvalidOperationException(
                $"Oracle bulk copy column count mismatch for {targetSchema}.{table}: " +
                $"reader={reader.FieldCount}, target={targetColCount}. Check projection and staging columns.");
        }

        // Column mappings: use ordinal mapping to avoid quoted identifier issues in ODP.NET.
        for (var i = 0; i < reader.FieldCount; i++)
        {
            bulk.ColumnMappings.Add(i, i);
        }

        bulk.WriteToServer(reader);

        OracleBulkCopyOptions ctxBulkCopyOptions()
        {
            var o = OracleBulkCopyOptions.Default;
            if (_requestAccessor?.Invoke()?.BulkCopyUseInternalTransaction == true)
                o |= OracleBulkCopyOptions.UseInternalTransaction;
            return o;
        }
        int ctBatchSize() => _requestAccessor?.Invoke()?.BulkCopyBatchSize ?? 5000;
        int ctBulkTimeout() => _requestAccessor?.Invoke()?.BulkCopyTimeoutSeconds ?? 0;
    }

    private static async Task<int> GetOracleTargetColumnCountAsync(
        OracleConnection conn,
        string targetSchema,
        string targetTable,
        CancellationToken ct)
    {
        var ownerRaw = (targetSchema ?? string.Empty).Trim().Trim('"');
        var tableRaw = (targetTable ?? string.Empty).Trim().Trim('"');
        var ownerUpper = ownerRaw.ToUpperInvariant();
        var tableUpper = tableRaw.ToUpperInvariant();

        await using var cmd = conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = @"
SELECT COUNT(*)
FROM all_tab_columns
WHERE (owner = :p_owner_raw OR owner = :p_owner_upper)
  AND (table_name = :p_table_raw OR table_name = :p_table_upper)";
        cmd.Parameters.Add(new OracleParameter("p_owner_raw", OracleDbType.Varchar2, ownerRaw, System.Data.ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("p_owner_upper", OracleDbType.Varchar2, ownerUpper, System.Data.ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("p_table_raw", OracleDbType.Varchar2, tableRaw, System.Data.ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("p_table_upper", OracleDbType.Varchar2, tableUpper, System.Data.ParameterDirection.Input));

        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt32(v);
    }

    // The core engine doesn't retain the MigrationRequest; we capture it via a lightweight accessor for bulk settings.
    private Func<MigrationRequest?>? _requestAccessor;

    private void SetRequestAccessor(Func<MigrationRequest?> accessor) => _requestAccessor = accessor;
}
