using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

namespace SqlToOracleMigrator.Core;

public static class SqlIdent
{
    public static string Bracket(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Identifier is required.", nameof(name));
        return "[" + name.Replace("]", "]]") + "]";
    }
}

public static class OracleIdent
{
    /// <summary>
    /// Force-quotes an identifier and escapes embedded double-quotes by doubling them.
    /// </summary>
    public static string QuoteIdent(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Identifier is required.", nameof(name));
        var n = name.Trim();
        return "\"" + n.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Formats a schema/user name safely for use as the schema prefix in SQL.
    /// 
    /// Important: Oracle usernames/schemas are typically stored as UPPERCASE when created unquoted.
    /// Quoting a normal username (e.g., "system") makes it case-sensitive and commonly fails.
    /// </summary>
    public static string FormatSchema(string schemaOrUser)
    {
        if (string.IsNullOrWhiteSpace(schemaOrUser)) throw new ArgumentException("Schema/user is required.", nameof(schemaOrUser));
        var s = schemaOrUser.Trim();

        // If user explicitly provided quotes, keep them as-is.
        if (IsQuoted(s)) return s;

        // Unquoted identifiers are case-insensitive and resolved as UPPERCASE in Oracle.
        return s.ToUpperInvariant();
    }

    /// <summary>
    /// Normalizes a schema/user name for dictionary lookups (ALL_USERS.USERNAME).
    /// </summary>
    public static string NormalizeSchemaForLookup(string schemaOrUser)
    {
        if (string.IsNullOrWhiteSpace(schemaOrUser)) return string.Empty;
        var s = schemaOrUser.Trim();
        if (IsQuoted(s))
        {
            // Remove outer quotes and unescape doubled quotes.
            var inner = s.Substring(1, s.Length - 2);
            return inner.Replace("\"\"", "\"");
        }
        return s.ToUpperInvariant();
    }

    
    /// <summary>
    /// Returns true if the name can be used as an unquoted Oracle identifier.
    /// Oracle resolves unquoted identifiers as UPPERCASE, and SIMPLE_SQL_NAME disallows quotes/dots/spaces.
    /// </summary>
    public static bool IsSafeUnquoted(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();

        // If already quoted, treat as not safe for unquoted usage.
        if (IsQuoted(n)) return false;

        // Must start with a letter.
        if (!char.IsLetter(n[0])) return false;

        for (int i = 1; i < n.Length; i++)
        {
            var c = n[i];
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Formats an object identifier (table/column/index/constraint) either as unquoted UPPERCASE (preferred)
    /// or as a quoted identifier when required.
    /// </summary>
    public static string FormatObject(string name, bool preferUnquotedUppercase)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Identifier is required.", nameof(name));
        var n = name.Trim();

        if (preferUnquotedUppercase && IsSafeUnquoted(n))
            return n.ToUpperInvariant();

        // If user explicitly provided quotes, keep as-is.
        if (IsQuoted(n)) return n;

        return QuoteIdent(n);
    }

private static bool IsQuoted(string s)
        => s.Length >= 2 && s.StartsWith('"') && s.EndsWith('"');
}
