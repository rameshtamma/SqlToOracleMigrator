using Microsoft.Data.SqlClient;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    // Internal schemas created by the migrator in the source SQL database.
    // Exclude them from discovery so the tool does not migrate its own tracking metadata.
    private static readonly HashSet<string> _internalSqlSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "ToolMig"
    };

    private static bool IsInternalToolSchema(string? schema)
        => !string.IsNullOrWhiteSpace(schema) && _internalSqlSchemas.Contains(schema.Trim());

    private async Task<List<(string Schema, string Name)>> DiscoverSequencesAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT s.name, seq.name
FROM {db}.sys.sequences seq
JOIN {db}.sys.schemas s ON seq.schema_id = s.schema_id
WHERE seq.is_ms_shipped = 0
ORDER BY s.name, seq.name;";

        await using var cmd = new SqlCommand(sql, openSql);
        var list = new List<(string Schema, string Name)>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var schema = rdr.GetString(0);
            if (IsInternalToolSchema(schema)) continue;
            list.Add((schema, rdr.GetString(1)));
        }
        return list;
    }

    private async Task<List<(string Schema, string Name)>> DiscoverViewsAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT s.name, v.name
FROM {db}.sys.views v
JOIN {db}.sys.schemas s ON v.schema_id = s.schema_id
WHERE v.is_ms_shipped = 0
ORDER BY s.name, v.name;";

        await using var cmd = new SqlCommand(sql, openSql);
        var list = new List<(string Schema, string Name)>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var schema = rdr.GetString(0);
            if (IsInternalToolSchema(schema)) continue;
            list.Add((schema, rdr.GetString(1)));
        }
        return list;
    }

    private async Task<List<(string Schema, string Name)>> DiscoverProceduresAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT s.name, p.name
FROM {db}.sys.procedures p
JOIN {db}.sys.schemas s ON p.schema_id = s.schema_id
WHERE p.is_ms_shipped = 0
ORDER BY s.name, p.name;";

        await using var cmd = new SqlCommand(sql, openSql);
        var list = new List<(string Schema, string Name)>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var schema = rdr.GetString(0);
            if (IsInternalToolSchema(schema)) continue;
            list.Add((schema, rdr.GetString(1)));
        }
        return list;
    }

    private async Task<List<(string Schema, string Name)>> DiscoverFunctionsAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT s.name, o.name
FROM {db}.sys.objects o
JOIN {db}.sys.schemas s ON o.schema_id = s.schema_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('FN','TF','IF')
ORDER BY s.name, o.name;";

        await using var cmd = new SqlCommand(sql, openSql);
        var list = new List<(string Schema, string Name)>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var schema = rdr.GetString(0);
            if (IsInternalToolSchema(schema)) continue;
            list.Add((schema, rdr.GetString(1)));
        }
        return list;
    }

    private async Task<List<(string Schema, string Name, string ParentSchema, string ParentName)>> DiscoverTriggersAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        // NOTE: sys.triggers does NOT expose schema_id in SQL Server; schema is on sys.objects.
        // Join through sys.objects (trigger object) to resolve trigger schema.
        var sql = $@"SELECT ts.name AS TriggerSchema, tr.name AS TriggerName,
       ps.name AS ParentSchema, po.name AS ParentName
FROM {db}.sys.triggers tr
JOIN {db}.sys.objects tro ON tr.object_id = tro.object_id
JOIN {db}.sys.schemas ts ON tro.schema_id = ts.schema_id
JOIN {db}.sys.objects po ON tr.parent_id = po.object_id
JOIN {db}.sys.schemas ps ON po.schema_id = ps.schema_id
WHERE tr.is_ms_shipped = 0
ORDER BY ts.name, tr.name;";

        await using var cmd = new SqlCommand(sql, openSql);
        var list = new List<(string Schema, string Name, string ParentSchema, string ParentName)>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var trgSchema = rdr.GetString(0);
            var parentSchema = rdr.GetString(2);
            if (IsInternalToolSchema(trgSchema) || IsInternalToolSchema(parentSchema)) continue;
            list.Add((trgSchema, rdr.GetString(1), parentSchema, rdr.GetString(3)));
        }
        return list;
    }

    private async Task<List<(string Schema, string Name, string BaseObjectName)>> DiscoverSynonymsAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT s.name, sy.name, sy.base_object_name
FROM {db}.sys.synonyms sy
JOIN {db}.sys.schemas s ON sy.schema_id = s.schema_id
ORDER BY s.name, sy.name;";

        await using var cmd = new SqlCommand(sql, openSql);
        var list = new List<(string Schema, string Name, string BaseObjectName)>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var schema = rdr.GetString(0);
            if (IsInternalToolSchema(schema)) continue;
            list.Add((schema, rdr.GetString(1), rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2)));
        }
        return list;
    }

    private async Task<List<(string Schema, string Name, string UnderlyingType)>> DiscoverUserDefinedTypesAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT s.name, t.name, bt.name AS BaseType
FROM {db}.sys.types t
JOIN {db}.sys.schemas s ON t.schema_id = s.schema_id
LEFT JOIN {db}.sys.types bt ON t.system_type_id = bt.user_type_id AND bt.is_user_defined = 0
WHERE t.is_user_defined = 1
ORDER BY s.name, t.name;";

        await using var cmd = new SqlCommand(sql, openSql);
        var list = new List<(string Schema, string Name, string UnderlyingType)>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var schema = rdr.GetString(0);
            if (IsInternalToolSchema(schema)) continue;
            list.Add((schema, rdr.GetString(1), rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2)));
        }
        return list;
    }

    private async Task<List<SqlForeignKeyDef>> DiscoverForeignKeysAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT fs.name AS FkSchema, fk.name AS FkName,
       ts.name AS TableSchema, t.name AS TableName,
       rs.name AS RefSchema, rt.name AS RefTable,
       c.name AS ColName, rc.name AS RefColName,
       fkc.constraint_column_id AS Ordinal,
       fk.delete_referential_action_desc AS OnDelete
FROM {db}.sys.foreign_keys fk
JOIN {db}.sys.schemas fs ON fk.schema_id = fs.schema_id
JOIN {db}.sys.tables t ON fk.parent_object_id = t.object_id
JOIN {db}.sys.schemas ts ON t.schema_id = ts.schema_id
JOIN {db}.sys.tables rt ON fk.referenced_object_id = rt.object_id
JOIN {db}.sys.schemas rs ON rt.schema_id = rs.schema_id
JOIN {db}.sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN {db}.sys.columns c ON c.object_id = t.object_id AND c.column_id = fkc.parent_column_id
JOIN {db}.sys.columns rc ON rc.object_id = rt.object_id AND rc.column_id = fkc.referenced_column_id
WHERE fk.is_ms_shipped = 0
ORDER BY fs.name, fk.name, fkc.constraint_column_id;";

        await using var cmd = new SqlCommand(sql, openSql);
        var dict = new Dictionary<string, (string fs, string fkname, string ts, string t, string rs, string rt, string? ondel, List<SqlForeignKeyColumnPair> cols)>(StringComparer.OrdinalIgnoreCase);
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var fs = rdr.GetString(0);
            var fkname = rdr.GetString(1);
            var ts = rdr.GetString(2);
            var t = rdr.GetString(3);
            var rs = rdr.GetString(4);
            var rt = rdr.GetString(5);

            // Skip tool-owned schemas.
            if (IsInternalToolSchema(fs) || IsInternalToolSchema(ts) || IsInternalToolSchema(rs))
                continue;
            var col = rdr.GetString(6);
            var rcol = rdr.GetString(7);
            var ord = rdr.GetInt32(8);
            var ondel = rdr.IsDBNull(9) ? null : rdr.GetString(9);

            var key = fs + "." + fkname;
            if (!dict.TryGetValue(key, out var v))
            {
                v = (fs, fkname, ts, t, rs, rt, ondel, new List<SqlForeignKeyColumnPair>());
                dict[key] = v;
            }
            v.cols.Add(new SqlForeignKeyColumnPair(col, rcol, ord));
            dict[key] = v;
        }

        var list = new List<SqlForeignKeyDef>();
        foreach (var v in dict.Values)
        {
            list.Add(new SqlForeignKeyDef(
                Schema: v.fs,
                Name: v.fkname,
                TableSchema: v.ts,
                TableName: v.t,
                RefTableSchema: v.rs,
                RefTableName: v.rt,
                Columns: v.cols.OrderBy(c => c.Ordinal).ToList(),
                OnDeleteAction: v.ondel));
        }
        return list;
    }
}
