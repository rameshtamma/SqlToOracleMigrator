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
    // Converts SQL Server DEFAULT definitions into Oracle-compatible DEFAULT expressions.
    // Goal: preserve intent while staying within Oracle DEFAULT-expression constraints.
    public static string? Convert(string? sqlDefaultDefinition)
    {
        if (string.IsNullOrWhiteSpace(sqlDefaultDefinition)) return null;

        // SQL Server default constraints are commonly wrapped in parentheses (sometimes multiple levels).
        var s = sqlDefaultDefinition.Trim();
        while (s.StartsWith("(") && s.EndsWith(")") && s.Length > 2)
        {
            var inner = s.Substring(1, s.Length - 2).Trim();
            if (!IsBalanced(inner)) break;
            s = inner;
        }

        // Normalize bracket quoting early: [dbo].[X] -> dbo.X
        s = s.Replace("[", "").Replace("]", "");

        // Remove Unicode string literal prefix: N'abc' => 'abc'
        if (s.StartsWith("N'", StringComparison.OrdinalIgnoreCase))
            s = "'" + s.Substring(2);

        // Strip common SQL Server wrapper functions that appear in defaults (WideWorldImporters).
        s = StripConvertWrapper(s);
        s = StripCastWrapper(s);

        // Normalize common function-style tokens without parentheses
        // (some systems store GETDATE rather than GETDATE()).
        if (string.Equals(s, "GETDATE()", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "GETDATE", StringComparison.OrdinalIgnoreCase))
            return "SYSDATE";

        if (string.Equals(s, "SYSDATETIME()", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "SYSDATETIME", StringComparison.OrdinalIgnoreCase))
            return "SYSTIMESTAMP";

        if (string.Equals(s, "GETUTCDATE()", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "GETUTCDATE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "SYSUTCDATETIME()", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "SYSUTCDATETIME", StringComparison.OrdinalIgnoreCase))
            return "SYS_EXTRACT_UTC(SYSTIMESTAMP)";

        if (string.Equals(s, "NEWID()", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "NEWID", StringComparison.OrdinalIgnoreCase))
            return "SYS_GUID()";

        // SQL Server sequences: NEXT VALUE FOR schema.sequence -> schema.sequence.NEXTVAL
        var nextVal = TryConvertNextValueFor(s);
        if (!string.IsNullOrWhiteSpace(nextVal))
            return nextVal;

        // Bit/boolean typical defaults (keep as-is)
        if (s.Equals("1")) return "1";
        if (s.Equals("0")) return "0";

        return s;
    }

    private static string StripConvertWrapper(string s)
    {
        // CONVERT(targetType, expr [, style]) -> expr
        // We only need this for defaults; we retain the inner expression.
        // Handles cases like: CONVERT(datetime2(7),sysutcdatetime())
        for (var i = 0; i < 5; i++) // limit recursion
        {
            var trimmed = s.Trim();
            if (!trimmed.StartsWith("CONVERT(", StringComparison.OrdinalIgnoreCase))
                return s;

            var inner = ExtractParenInner(trimmed, "CONVERT");
            if (inner is null) return s;

            var args = SplitTopLevelArgs(inner);
            if (args.Count < 2) return s;

            s = args[1].Trim();
        }
        return s;
    }

    private static string StripCastWrapper(string s)
    {
        // CAST(expr AS type) -> expr
        for (var i = 0; i < 5; i++)
        {
            var trimmed = s.Trim();
            if (!trimmed.StartsWith("CAST(", StringComparison.OrdinalIgnoreCase))
                return s;

            var inner = ExtractParenInner(trimmed, "CAST");
            if (inner is null) return s;

            // Find top-level " AS " (not inside parentheses)
            var depth = 0;
            for (var idx = 0; idx <= inner.Length - 4; idx++)
            {
                var ch = inner[idx];
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                if (depth != 0) continue;

                if (inner.AsSpan(idx).StartsWith(" AS ", StringComparison.OrdinalIgnoreCase))
                {
                    s = inner.Substring(0, idx).Trim();
                    goto NEXT;
                }
            }
            return s;
        NEXT:
            continue;
        }
        return s;
    }

    private static string? TryConvertNextValueFor(string s)
    {
        // Accept forms:
        // NEXT VALUE FOR schema.sequence
        // NEXT VALUE FOR sequence
        var idx = s.IndexOf("NEXT VALUE FOR", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var after = s.Substring(idx + "NEXT VALUE FOR".Length).Trim();
        if (string.IsNullOrWhiteSpace(after)) return null;

        // Remove any trailing tokens (rare); keep identifier-ish prefix (letters/digits/_/.)
        var ident = new string(after.TakeWhile(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '.').ToArray());
        if (string.IsNullOrWhiteSpace(ident)) return null;

        return $"{ident}.NEXTVAL";
    }

    private static string? ExtractParenInner(string s, string funcName)
    {
        // expects FUNC(...)
        var open = s.IndexOf('(');
        if (open < 0) return null;
        var close = FindMatchingParen(s, open);
        if (close < 0) return null;
        return s.Substring(open + 1, close - open - 1);
    }

    private static int FindMatchingParen(string s, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static List<string> SplitTopLevelArgs(string s)
    {
        var list = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (ch == ',' && depth == 0)
            {
                list.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        list.Add(s.Substring(start));
        return list;
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
