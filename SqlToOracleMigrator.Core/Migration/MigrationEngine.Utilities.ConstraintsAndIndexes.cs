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

        // Drop-and-recreate pattern for determinism.
        foreach (var k in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DropOracleConstraintIfExistsAsync(openOra, targetSchema, table, k.Name, cancellationToken);
            var ddl = BuildOracleConstraintDdl(targetSchema, table, k);
            await ExecuteOracleIgnoreAsync(openOra, ddl, cancellationToken);
        }

        foreach (var ix in indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DropOracleIndexIfExistsAsync(openOra, targetSchema, ix.Name, cancellationToken);
            var ddl = BuildOracleIndexDdl(targetSchema, table, ix);
            await ExecuteOracleIgnoreAsync(openOra, ddl, cancellationToken);
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
            var ord = rdr.GetInt32(3);

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
            var ord = rdr.GetInt32(3);

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
