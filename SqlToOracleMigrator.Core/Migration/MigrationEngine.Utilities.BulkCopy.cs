using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using System.Linq;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private static readonly HashSet<string> BulkUnsafeSqlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "time",
        "datetimeoffset",
        "datetime2"
    };

    private static bool TryGetBulkUnsafeTypes(
        IReadOnlyList<SqlTableColumn> columns,
        out List<string> types)
    {
        types = columns
            .Select(c => c.SqlTypeName)
            .Where(t => !string.IsNullOrWhiteSpace(t) && BulkUnsafeSqlTypes.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return types.Count > 0;
    }

    private async Task CopyTableBulkAsync(SqlConnection openSql, OracleConnection openOra, string dbName, string schema, string table, string targetSchema, CancellationToken ct)
    {
        var columns = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, ct);
        if (columns.Count == 0) return;

        // PERMANENT GUARD: OracleBulkCopy has known driver-level NullReferenceException bugs when consuming
        // certain source CLR/SqlTypes (most commonly SQL Server TIME -> Oracle INTERVAL DAY TO SECOND, and
        // DateTimeOffset -> TIMESTAMP WITH TIME ZONE). The non-bulk path normalizes these values safely.
        // To avoid flaky driver crashes, automatically fall back to the stage-aware insert path for tables
        // that contain these types.
        if (TryGetBulkUnsafeTypes(columns, out var unsafeTypes))
        {
            _logger?.Info($"[BulkCopy] Table {schema}.{table} contains bulk-unsafe types ({string.Join(",", unsafeTypes)}); using stage-aware inserts (bulk disabled for this table)." );
            await CopyTableAsync(openSql, openOra, dbName, schema, table, targetSchema, ct);
            return;
        }

        // Build projection with safe casts and staging columns for XML/spatial where needed.
        var selectSql = BuildSqlSelectForTable(schema, table, columns, useTopN: false);

        await using var selectCmd = new SqlCommand(selectSql, openSql) { CommandTimeout = 0 };
        await using var reader = await selectCmd.ExecuteReaderAsync(ct);

        var preferUnquotedUpper = _requestAccessor?.Invoke()?.UseUnquotedUppercaseIdentifiers ?? true;
        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);

        // OracleBulkCopy internally validates DestinationTableName using DBMS_ASSERT. In many environments this
        // uses SIMPLE_SQL_NAME semantics, which rejects dots and quoted identifiers (causing ORA-44003).
        // When preferUnquotedUpper is enabled, we set CURRENT_SCHEMA and pass only the unqualified table name.
        var destTable = preferUnquotedUpper
            ? OracleIdent.FormatObject(table, preferUnquotedUpper)
            : $"{schemaPrefix}.{OracleIdent.QuoteIdent(table)}";

        if (preferUnquotedUpper)
        {
            await using var setSchema = new OracleCommand($"ALTER SESSION SET CURRENT_SCHEMA = {schemaPrefix}", openOra);
            await setSchema.ExecuteNonQueryAsync(ct);
        }

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
        try
        {
            bulk.WriteToServer(reader);
        }
        catch (OracleException ex) when (ex.Number == 44003)
        {
            await BulkFallbackAsync($"BulkCopy rejected destination table name '{destTable}' (ORA-44003).", ex);
            return;
        }
        catch (NullReferenceException ex)
        {
            // Driver-level bug inside Oracle.ManagedDataAccess (OracleBulkCopy) – do NOT fail the run.
            // Fall back to the safe path which normalizes values.
            await BulkFallbackAsync("OracleBulkCopy hit internal NullReferenceException (ODP.NET bug).", ex);
            return;
        }
        catch (AggregateException ex) when (ex.InnerException is NullReferenceException)
        {
            await BulkFallbackAsync("OracleBulkCopy hit internal NullReferenceException (ODP.NET bug).", ex.InnerException!);
            return;
        }

        async Task BulkFallbackAsync(string reason, Exception ex)
        {
            _logger?.Warn($"[BulkCopy] {reason} Falling back to stage-aware inserts for {schema}.{table}. {ex.GetType().Name}: {ex.Message}");

            // IMPORTANT: do NOT use openOra.ConnectionString here because ODP.NET can strip the Password
            // from ConnectionString after Open() unless Persist Security Info=true, which leads to ORA-01005.
            var req = _requestAccessor?.Invoke();
            var fallbackConnStr = req is null
                ? openOra.ConnectionString
                : ConnectionStringBuilders.BuildOracle(req.TargetOracleConnection);

            await using var fallbackOra = new OracleConnection(fallbackConnStr);
            await fallbackOra.OpenAsync(ct);

            await CopyTableAsync(openSql, fallbackOra, dbName, schema, table, targetSchema, ct);
        }

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
