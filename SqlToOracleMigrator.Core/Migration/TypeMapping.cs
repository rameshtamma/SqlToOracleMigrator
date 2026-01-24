namespace SqlToOracleMigrator.Core;

public sealed class SqlToOracleTypeMapper
{
    private readonly DataTypeMappingConfig _config;

    public SqlToOracleTypeMapper(DataTypeMappingConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string Map(string sqlTypeName, int? maxLength, int? precision, int? scale)
    {
        if (string.IsNullOrWhiteSpace(sqlTypeName))
            return "VARCHAR2(4000)";

        var key = sqlTypeName.Trim().ToLowerInvariant();

        // Handle MAX / unbounded types up-front (SQL Server uses max_length = -1 for (n)varchar(max), varbinary(max), etc.)
        if (maxLength is not null && maxLength.Value < 0)
        {
            return key switch
            {
                "nvarchar" or "ntext" => "NCLOB",
                "varchar" or "text" or "xml" => "CLOB",
                "varbinary" or "image" => "BLOB",
                _ => "CLOB"
            };
        }

        if (!_config.SqlToOracle.TryGetValue(key, out var template) || string.IsNullOrWhiteSpace(template))
            template = "VARCHAR2(4000)";

        // Token replacement inputs
        var len = maxLength ?? 0;
        if (len < 0) len = 0;

        // nvarchar/nchar maxLength from sys.columns is in bytes; for nvarchar it's bytes/2
        if (key is "nvarchar" or "nchar" && len > 0)
            len = len / 2;

        var p = precision ?? 0;
        var s = scale ?? 0;

        // Before rendering template, enforce Oracle datatype max length rules to avoid ORA-00910.
        // - VARCHAR2 max 4000 bytes (in SQL DDL; extended data types require special DB settings)
        // - NVARCHAR2 max 2000 characters
        // - RAW max 2000 bytes
        // If length exceeds limits, map to LOB equivalents.
        if (key is "varchar" && len > 4000) return "CLOB";
        if (key is "nvarchar" && len > 2000) return "NCLOB";
        if (key is "char" && len > 2000) return "CLOB";
        if (key is "nchar" && len > 2000) return "NCLOB";
        if (key is "binary" && len > 2000) return "BLOB";

        // Render template
        var rendered = template
            .Replace("{len}", len <= 0 ? (key is "nvarchar" ? "2000" : "4000") : len.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{precision}", p <= 0 ? "38" : p.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{scale}", s < 0 ? "0" : s.ToString(), StringComparison.OrdinalIgnoreCase);

        // Post-guard: if template produced over-limit VARCHAR2/NVARCHAR2/RAW, downgrade to LOB/RAW-safe.
        var upper = rendered.ToUpperInvariant();
        if (upper.StartsWith("VARCHAR2(") && ExtractLen(upper) is int vlen && vlen > 4000) return "CLOB";
        if (upper.StartsWith("NVARCHAR2(") && ExtractLen(upper) is int nvlen && nvlen > 2000) return "NCLOB";
        if (upper.StartsWith("RAW(") && ExtractLen(upper) is int rlen && rlen > 2000) return "BLOB";

        return rendered;
    }

    private static int? ExtractLen(string rendered)
    {
        var open = rendered.IndexOf('(');
        var close = rendered.IndexOf(')');
        if (open < 0 || close <= open) return null;
        var inner = rendered.Substring(open + 1, close - open - 1);
        // NUMBER(precision,scale) -> take first part only; for length-based types it's single value
        var part = inner.Split(',')[0].Trim();
        return int.TryParse(part, out var n) ? n : null;
    }
}
public static class OracleDdlGenerator
{
    public static string CreateTableDdl(string targetSchema, string tableName, IReadOnlyList<SqlTableColumn> columns, SqlToOracleTypeMapper mapper)
    {
        if (string.IsNullOrWhiteSpace(targetSchema)) throw new ArgumentNullException(nameof(targetSchema));
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentNullException(nameof(tableName));
        if (columns is null || columns.Count == 0) throw new InvalidOperationException("No columns found.");

        // Schema/user should typically be unquoted (Oracle resolves unquoted identifiers as UPPERCASE).
        // Quoting a normal username (e.g., "system") makes it case-sensitive and commonly fails.
        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var tableQ = OracleIdent.QuoteIdent(tableName);

        var colLines = columns
            .OrderBy(c => c.Ordinal)
            .Select(c =>
            {
                var colQ = OracleIdent.QuoteIdent(c.ColumnName);
                var oracleType = mapper.Map(c.SqlTypeName, c.MaxLength, c.Precision, c.Scale);
                var def = OracleDefaultConverter.Convert(c.DefaultDefinition);
                var defClause = string.IsNullOrWhiteSpace(def) ? "" : $" DEFAULT {def}";
                var nullable = c.IsNullable ? "" : " NOT NULL";
                return $"{colQ} {oracleType}{defClause}{nullable}";
            });

        return $"CREATE TABLE {schemaQ}.{tableQ} (\n  {string.Join(",\n  ", colLines)}\n)";
    }
}


internal static class OracleDefaultConverter
{
    public static string? Convert(string? sqlDefaultDefinition)
    {
        if (string.IsNullOrWhiteSpace(sqlDefaultDefinition)) return null;

        // SQL Server default constraints are commonly wrapped in parentheses (sometimes multiple levels).
        var s = sqlDefaultDefinition.Trim();
        while (s.StartsWith("(") && s.EndsWith(")") && s.Length > 2)
        {
            var inner = s.Substring(1, s.Length - 2).Trim();
            // Stop unwrapping if parentheses are imbalanced.
            if (!IsBalanced(inner)) break;
            s = inner;
        }

        // Remove Unicode string literal prefix: N'abc' => 'abc'
        if (s.StartsWith("N'", StringComparison.OrdinalIgnoreCase))
            s = "'" + s.Substring(2);

        // Common function mappings
        if (s.Equals("GETDATE()", StringComparison.OrdinalIgnoreCase) || s.Equals("GETDATE", StringComparison.OrdinalIgnoreCase))
            return "SYSDATE";
        if (s.Equals("NEWID()", StringComparison.OrdinalIgnoreCase) || s.Equals("NEWID", StringComparison.OrdinalIgnoreCase))
            return "SYS_GUID()";

        // Bit/boolean typical defaults
        if (s.Equals("1")) return "1";
        if (s.Equals("0")) return "0";

        // Remove SQL Server bracket quoting in literals/idents; keep as-is otherwise.
        s = s.Replace("[", "").Replace("]", "");

        return s;
    }

    private static bool IsBalanced(string s)
    {
        var depth = 0;
        foreach (var ch in s)
        {
            if (ch == '(') depth++;
            else if (ch == ')')
            {
                depth--;
                if (depth < 0) return false;
            }
        }
        return depth == 0;
    }
}
