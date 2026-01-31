using System.Linq;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private sealed record SqlKeyConstraint(string Name, string Type, IReadOnlyList<string> Columns);

    private sealed record SqlIndexDef(string Name, bool IsUnique, IReadOnlyList<string> KeyColumns);

    private async Task DeployConstraintsAndIndexesAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string sourceSchema,
        string table,
        string targetSchema,
        CancellationToken cancellationToken)
    {
        var keys = await GetSqlKeyConstraintsAsync(openSql, dbName, sourceSchema, table, cancellationToken);
        var indexes = await GetSqlIndexesAsync(openSql, dbName, sourceSchema, table, cancellationToken);

        // Oracle automatically creates a backing index for PRIMARY KEY / UNIQUE constraints.
        // If the source also exposes that backing index (or a duplicate index on the same column list),
        // attempting to create it explicitly can fail with ORA-01408 (such column list already indexed).
        // To keep the run deterministic and avoid false failures, skip creating any index whose name
        // matches a PK/UQ constraint name.
        var keyConstraintNames = new HashSet<string>(keys.Select(k => k.Name), StringComparer.OrdinalIgnoreCase);

        // Some Oracle environments (esp. XE) may fail index/constraint creation for certain SQL Server definitions.
        // - ORA-02327: index on LOB column
        // - ORA-01450: maximum key length exceeded (wide composite index)
        // We treat these as non-fatal warnings so the migration can continue.
        var oraCols = await GetOracleColumnInfoAsync(openOra, targetSchema, table, cancellationToken);

        // Drop-and-recreate pattern for determinism.
        foreach (var k in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DropOracleConstraintIfExistsAsync(openOra, targetSchema, table, k.Name, cancellationToken);
            var ddl = BuildOracleConstraintDdl(targetSchema, table, k);

            // Skip constraints that would require an index on a LOB column.
            if (ContainsLobColumn(k.Columns, oraCols, out var lobCol))
            {
                _logger.Warn($"[DdlGeneration][WARN] TABLE {sourceSchema}.{table}: Skipping constraint '{k.Name}' because it includes LOB column '{lobCol}'.");
                continue;
            }

            if (!await TryExecuteOracleIndexLikeDdlAsync(openOra, ddl, sourceSchema, table, $"constraint '{k.Name}'", cancellationToken))
                continue;
        }

        foreach (var ix in indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (keyConstraintNames.Contains(ix.Name))
            {
                _logger.Warn($"[DdlGeneration][WARN] TABLE {sourceSchema}.{table}: Skipping index '{ix.Name}' because an identically named PK/UQ constraint was created (Oracle will create a backing index automatically).");
                continue;
            }

            await DropOracleIndexIfExistsAsync(openOra, targetSchema, ix.Name, cancellationToken);
            var ddl = BuildOracleIndexDdl(targetSchema, table, ix);

            if (ContainsLobColumn(ix.KeyColumns, oraCols, out var lobCol))
            {
                _logger.Warn($"[DdlGeneration][WARN] TABLE {sourceSchema}.{table}: Skipping index '{ix.Name}' because it includes LOB column '{lobCol}'.");
                continue;
            }

            if (!await TryExecuteOracleIndexLikeDdlAsync(openOra, ddl, sourceSchema, table, $"index '{ix.Name}'", cancellationToken))
                continue;
        }
    }

    private sealed record OracleColumnInfo(string DataType, int DataLength);

    private static bool IsLobType(string dataType)
        => dataType.Equals("BLOB", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("CLOB", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("NCLOB", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("LONG", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("LONG RAW", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsLobColumn(IReadOnlyList<string> cols, Dictionary<string, OracleColumnInfo> oraCols, out string lobCol)
    {
        foreach (var c in cols)
        {
            if (oraCols.TryGetValue(c, out var info) && IsLobType(info.DataType))
            {
                lobCol = c;
                return true;
            }
        }
        lobCol = string.Empty;
        return false;
    }

    private static string NormalizeOraObject(string s) => s.Trim().Trim('"').ToUpperInvariant();

    private static async Task<Dictionary<string, OracleColumnInfo>> GetOracleColumnInfoAsync(
        OracleConnection openOra,
        string targetSchema,
        string table,
        CancellationToken ct)
    {
        var dict = new Dictionary<string, OracleColumnInfo>(StringComparer.OrdinalIgnoreCase);

        // Use ALL_TAB_COLUMNS to support cross-schema provisioning.
        const string sql = @"SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH
FROM ALL_TAB_COLUMNS
WHERE OWNER = :p_owner AND TABLE_NAME = :p_table";

        await using var cmd = new OracleCommand(sql, openOra);
        cmd.Parameters.Add(new OracleParameter("p_owner", NormalizeOraObject(targetSchema)));
        cmd.Parameters.Add(new OracleParameter("p_table", NormalizeOraObject(table)));

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var col = rdr.GetString(0);
            var dt = rdr.GetString(1);
            var len = rdr.IsDBNull(2) ? 0 : Convert.ToInt32(rdr.GetValue(2));
            dict[col] = new OracleColumnInfo(dt, len);
        }

        return dict;
    }

    private async Task<bool> TryExecuteOracleIndexLikeDdlAsync(
        OracleConnection openOra,
        string ddl,
        string sourceSchema,
        string table,
        string objectLabel,
        CancellationToken ct)
    {
        try
        {
            await ExecuteOracleIgnoreAsync(openOra, ddl, ct);
            return true;
        }
        catch (OracleException ex) when (ex.Number == 1450 || ex.Message.Contains("ORA-01450", StringComparison.OrdinalIgnoreCase))
        {
            // Wide composite index/constraint: don't fail entire migration.
            _logger.Warn($"[DdlGeneration][WARN] TABLE {sourceSchema}.{table}: Skipping {objectLabel} due to ORA-01450 (maximum key length exceeded).");
            return false;
        }
        catch (OracleException ex) when (ex.Number == 2327 || ex.Message.Contains("ORA-02327", StringComparison.OrdinalIgnoreCase))
        {
            // Index on LOB expression/column.
            _logger.Warn($"[DdlGeneration][WARN] TABLE {sourceSchema}.{table}: Skipping {objectLabel} due to ORA-02327 (index on LOB/expression not supported).");
            return false;
        }
        catch (OracleException ex) when (ex.Number == 1408 || ex.Message.Contains("ORA-01408", StringComparison.OrdinalIgnoreCase))
        {
            // Duplicate index on the same column list (Oracle often creates a backing index for UNIQUE constraints).
            _logger.Warn($"[DdlGeneration][WARN] TABLE {sourceSchema}.{table}: Skipping {objectLabel} due to ORA-01408 (such column list already indexed).");
            return false;
        }
        catch (OracleException ex)
        {
            // Unknown error: preserve fail-fast semantics.
            _logger.Error($"[DdlGeneration][ERROR] TABLE {sourceSchema}.{table}: Failed to deploy {objectLabel}. {ex.Message}");
            throw;
        }
    }

    private async Task<List<SqlKeyConstraint>> GetSqlKeyConstraintsAsync(
        SqlConnection openSql,
        string dbName,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT kc.name AS ConstraintName, kc.type AS ConstraintType, c.name AS ColumnName, ic.key_ordinal AS Ordinal
FROM {db}.sys.key_constraints kc
JOIN {db}.sys.tables t ON kc.parent_object_id = t.object_id
JOIN {db}.sys.schemas s ON t.schema_id = s.schema_id
JOIN {db}.sys.index_columns ic ON ic.object_id = t.object_id AND ic.index_id = kc.unique_index_id
JOIN {db}.sys.columns c ON c.object_id = t.object_id AND c.column_id = ic.column_id
WHERE s.name = @SchemaName AND t.name = @TableName
ORDER BY kc.name, ic.key_ordinal;";

        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@SchemaName", schema);
        cmd.Parameters.AddWithValue("@TableName", table);

        var dict = new Dictionary<string, (string type, List<(int ord, string col)> cols)>(StringComparer.OrdinalIgnoreCase);
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var name = rdr.GetString(0);
            var type = rdr.GetString(1); // PK or UQ
            var col = rdr.GetString(2);
            // sys.index_columns.key_ordinal is a tinyint in SQL Server.
            // Some providers surface it as byte, so avoid GetInt32() to prevent InvalidCastException.
            var ord = Convert.ToInt32(rdr.GetValue(3));

            if (!dict.TryGetValue(name, out var v))
            {
                v = (type, new List<(int, string)>());
                dict[name] = v;
            }
            v.cols.Add((ord, col));
            dict[name] = v;
        }

        var list = new List<SqlKeyConstraint>();
        foreach (var kv in dict)
        {
            list.Add(new SqlKeyConstraint(
                Name: kv.Key,
                Type: kv.Value.type,
                Columns: kv.Value.cols.OrderBy(x => x.ord).Select(x => x.col).ToList()));
        }
        return list;
    }

    private async Task<List<SqlIndexDef>> GetSqlIndexesAsync(
        SqlConnection openSql,
        string dbName,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT ix.name AS IndexName, ix.is_unique AS IsUnique, c.name AS ColumnName, ic.key_ordinal AS Ordinal
FROM {db}.sys.indexes ix
JOIN {db}.sys.tables t ON ix.object_id = t.object_id
JOIN {db}.sys.schemas s ON t.schema_id = s.schema_id
JOIN {db}.sys.index_columns ic ON ic.object_id = t.object_id AND ic.index_id = ix.index_id AND ic.is_included_column = 0
JOIN {db}.sys.columns c ON c.object_id = t.object_id AND c.column_id = ic.column_id
WHERE s.name = @SchemaName AND t.name = @TableName
  AND ix.index_id > 0
  AND ix.is_primary_key = 0
  AND ix.is_unique_constraint = 0
  AND ix.is_hypothetical = 0
ORDER BY ix.name, ic.key_ordinal;";

        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@SchemaName", schema);
        cmd.Parameters.AddWithValue("@TableName", table);

        var dict = new Dictionary<string, (bool uniq, List<(int ord, string col)> cols)>(StringComparer.OrdinalIgnoreCase);
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var name = rdr.IsDBNull(0) ? string.Empty : rdr.GetString(0);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var uniq = rdr.GetBoolean(1);
            var col = rdr.GetString(2);
            // sys.index_columns.key_ordinal is a tinyint in SQL Server.
            var ord = Convert.ToInt32(rdr.GetValue(3));

            if (!dict.TryGetValue(name, out var v))
            {
                v = (uniq, new List<(int, string)>());
                dict[name] = v;
            }
            v.cols.Add((ord, col));
            dict[name] = v;
        }

        var list = new List<SqlIndexDef>();
        foreach (var kv in dict)
        {
            list.Add(new SqlIndexDef(
                Name: kv.Key,
                IsUnique: kv.Value.uniq,
                KeyColumns: kv.Value.cols.OrderBy(x => x.ord).Select(x => x.col).ToList()));
        }
        return list;
    }

    private static string BuildOracleConstraintDdl(string targetSchema, string table, SqlKeyConstraint k)
    {
        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var tableQ = OracleIdent.QuoteIdent(table);
        var cols = string.Join(",", k.Columns.Select(OracleIdent.QuoteIdent));

        var kind = k.Type.Equals("PK", StringComparison.OrdinalIgnoreCase) ? "PRIMARY KEY" : "UNIQUE";
        var nameQ = OracleIdent.QuoteIdent(k.Name);
        return $"ALTER TABLE {schemaQ}.{tableQ} ADD CONSTRAINT {nameQ} {kind} ({cols})";
    }

    private static string BuildOracleIndexDdl(string targetSchema, string table, SqlIndexDef ix)
    {
        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var tableQ = OracleIdent.QuoteIdent(table);
        var cols = string.Join(",", ix.KeyColumns.Select(OracleIdent.QuoteIdent));
        var uniq = ix.IsUnique ? "UNIQUE " : string.Empty;
        var nameQ = OracleIdent.QuoteIdent(ix.Name);
        return $"CREATE {uniq}INDEX {schemaQ}.{nameQ} ON {schemaQ}.{tableQ} ({cols})";
    }

    private static async Task DropOracleConstraintIfExistsAsync(OracleConnection openOra, string targetSchema, string table, string constraintName, CancellationToken ct)
    {
        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var tableQ = OracleIdent.QuoteIdent(table);
        var cQ = OracleIdent.QuoteIdent(constraintName);
        var plsql = $"BEGIN EXECUTE IMMEDIATE 'ALTER TABLE {schemaQ}.{tableQ} DROP CONSTRAINT {cQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using var cmd = new OracleCommand(plsql, openOra);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DropOracleIndexIfExistsAsync(OracleConnection openOra, string targetSchema, string indexName, CancellationToken ct)
    {
        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var iQ = OracleIdent.QuoteIdent(indexName);
        var plsql = $"BEGIN EXECUTE IMMEDIATE 'DROP INDEX {schemaQ}.{iQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using var cmd = new OracleCommand(plsql, openOra);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteOracleIgnoreAsync(OracleConnection openOra, string sql, CancellationToken ct)
    {
        await using var cmd = new OracleCommand(sql, openOra);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
