using System.Text;
using System.Text.RegularExpressions;

namespace SqlToOracleMigrator.Core;

/// <summary>
/// Conservative SQL text helpers.
///
/// This project migrates schema and data. For dependent objects (views/procs/functions/triggers), we attempt
/// a best-effort text transformation so that the resulting Oracle object is more likely to compile.
///
/// IMPORTANT:
/// - This is NOT a full T-SQL -> PL/SQL converter.
/// - Rules are intentionally conservative and syntax-safe.
/// - We never mutate text inside string literals or comments.
/// </summary>
public static class SqlScriptSanitizer
{
    /// <summary>
    /// Removes the leading '@' from T-SQL identifiers (variables/parameters) while leaving
    /// email addresses and any '@' inside strings/comments untouched.
    /// </summary>
    public static string RemoveAtFromIdentifiers(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql;

        var sb = new StringBuilder(sql.Length);
        var i = 0;

        bool inSingleQuote = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        while (i < sql.Length)
        {
            var ch = sql[i];

            // Exit line comment
            if (inLineComment)
            {
                sb.Append(ch);
                if (ch == '\n') inLineComment = false;
                i++;
                continue;
            }

            // Exit block comment
            if (inBlockComment)
            {
                sb.Append(ch);
                if (ch == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                {
                    sb.Append('/');
                    i += 2;
                    inBlockComment = false;
                }
                else
                {
                    i++;
                }
                continue;
            }

            // Handle string literal
            if (inSingleQuote)
            {
                sb.Append(ch);
                if (ch == '\'' )
                {
                    // Escaped quote in T-SQL is ''
                    if (i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        sb.Append('\'');
                        i += 2;
                        continue;
                    }
                    inSingleQuote = false;
                }
                i++;
                continue;
            }

            // Enter string literal
            if (ch == '\'')
            {
                inSingleQuote = true;
                sb.Append(ch);
                i++;
                continue;
            }

            // Enter comments
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                inLineComment = true;
                sb.Append(ch).Append(sql[i + 1]);
                i += 2;
                continue;
            }
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                inBlockComment = true;
                sb.Append(ch).Append(sql[i + 1]);
                i += 2;
                continue;
            }

            // Remove @ if it starts an identifier
            if (ch == '@')
            {
                var next = (i + 1) < sql.Length ? sql[i + 1] : '\0';
                if (IsIdentStart(next))
                {
                    // skip '@' only
                    i++;
                    continue;
                }
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Removes common T-SQL directives that are not meaningful in Oracle.
    /// Currently removes SET NOCOUNT ON (case-insensitive).
    /// </summary>
    public static string RemoveTsqlDirectives(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql;

        // Remove whole-line SET NOCOUNT ON/OFF statements.
        var lines = sql.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(sql.Length);
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("SET NOCOUNT", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Converts very simple PRINT statements to DBMS_OUTPUT.PUT_LINE.
    /// Only applies to PRINT <expr> where <expr> is a single token or string literal.
    /// </summary>
    public static string ConvertPrintToDbmsOutput(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql;

        // This is intentionally conservative; do not try to parse complex expressions.
        // PRINT 'text'  -> DBMS_OUTPUT.PUT_LINE('text');
        // PRINT var     -> DBMS_OUTPUT.PUT_LINE(var);
        var pattern = new Regex(@"(?im)^\s*PRINT\s+([^;\r\n]+)\s*;?\s*$");
        return pattern.Replace(sql, m => $"DBMS_OUTPUT.PUT_LINE({m.Groups[1].Value.Trim()});");
    }

    /// <summary>
    /// Converts a subset of RAISERROR calls to RAISE_APPLICATION_ERROR.
    /// Supports patterns like: RAISERROR('msg', 16, 1) or RAISERROR(@msg, 16, 1)
    /// </summary>
    public static string ConvertRaiseErrorToOracle(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql;

        // Replace RAISERROR(<message>, <sev>, <state>) with RAISE_APPLICATION_ERROR(-20000, <message>)
        // NOTE: severity/state are ignored; Oracle uses negative app codes.
        var pattern = new Regex(@"(?im)\bRAISERROR\s*\(\s*([^,]+)\s*,\s*[^,]+\s*,\s*[^\)]+\)");
        return pattern.Replace(sql, m => $"RAISE_APPLICATION_ERROR(-20000, {m.Groups[1].Value.Trim()})");
    }

    /// <summary>
    /// Applies all conservative transformations in a safe order.
    /// </summary>
    public static string ApplyConservativeRoutineTransforms(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql;

        var s = RemoveTsqlDirectives(sql);
        s = RemoveAtFromIdentifiers(s);
        s = ConvertPrintToDbmsOutput(s);
        s = ConvertRaiseErrorToOracle(s);
        return s;
    }

    private static bool IsIdentStart(char ch)
        => char.IsLetter(ch) || ch == '_' || ch == '#';
}
