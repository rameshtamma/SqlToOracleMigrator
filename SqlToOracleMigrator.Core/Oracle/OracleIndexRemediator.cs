using Oracle.ManagedDataAccess.Client;
using System.Text.RegularExpressions;

namespace SqlToOracleMigrator.Core.Oracle;

/// <summary>
/// Remediations for index-related Oracle errors.
///
/// Primary use case:
///  - ORA-01450: maximum key length exceeded
///
/// Strategy (safe default for migration tooling):
///  - Only attempt remediation for NON-UNIQUE indexes.
///  - Clamp large string columns using SUBSTR(col, 1, N) in a function-based index.
///  - Choose N so that the estimated key bytes fit under a conservative limit.
///
/// Notes:
///  - Oracle key length limits vary by block size and internal overhead.
///  - We use a conservative max of 3000 bytes for safety.
///  - For UNIQUE indexes/constraints, clamping changes semantics; we skip remediation.
/// </summary>
public static class OracleIndexRemediator
{
    private const int ConservativeMaxKeyBytes = 3000;

    private static readonly Regex RxCreateIndex = new(
        @"^\s*CREATE\s+(?<uniq>UNIQUE\s+)?INDEX\s+(?<idx>[^\s]+)\s+ON\s+(?<table>[^\(]+)\((?<cols>[^\)]+)\)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static async Task<string?> TryRemediateOra1450Async(
        OracleConnection openOra,
        string originalIndexSql,
        CancellationToken ct)
    {
        if (openOra is null) throw new ArgumentNullException(nameof(openOra));
        if (string.IsNullOrWhiteSpace(originalIndexSql)) return null;

        // We only support the generator's canonical form:
        // CREATE [UNIQUE] INDEX schema."IX" ON schema."TABLE" ("C1","C2",...)
        var m = RxCreateIndex.Match(originalIndexSql.Trim());
        if (!m.Success) return null;

        var isUnique = !string.IsNullOrWhiteSpace(m.Groups["uniq"].Value);
        if (isUnique) return null;

        var idx = m.Groups["idx"].Value.Trim();
        var tableRef = m.Groups["table"].Value.Trim();
        var colsRaw = m.Groups["cols"].Value.Trim();

        // Parse schema.table from the ON clause.
        // tableRef may contain schema.table or just table.
        var tableParts = tableRef.Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var schema = tableParts.Length == 2 ? Unquote(tableParts[0]) : null;
        var table = Unquote(tableParts.Length == 2 ? tableParts[1] : tableParts[0]);

        // Extract column identifiers.
        var cols = colsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unquote)
            .ToList();

        if (cols.Count == 0) return null;

        // Fetch Oracle column info (length/type) for byte-estimation.
        var colInfo = await ReadColumnInfoAsync(openOra, schema, table, ct);
        if (colInfo.Count == 0) return null;

        // Estimate key bytes and compute needed clamp for string columns.
        // We only clamp CHAR/VARCHAR2/NCHAR/NVARCHAR2.
        var parts = new List<(string col, int bytesPerChar, int maxChars, bool clampable)>();
        foreach (var c in cols)
        {
            if (!colInfo.TryGetValue(c, out var info))
            {
                // Unknown column (maybe expression); cannot remediate safely.
                return null;
            }

            var (dataType, charLength) = info;
            var dt = dataType.ToUpperInvariant();
            if (dt is "VARCHAR2" or "CHAR")
            {
                parts.Add((c, 1, charLength, clampable: true));
            }
            else if (dt is "NVARCHAR2" or "NCHAR")
            {
                parts.Add((c, 2, charLength, clampable: true));
            }
            else
            {
                // For NUMBER/DATE/etc: rough estimate small.
                parts.Add((c, 1, 30, clampable: false));
            }
        }

        int EstimateBytes(IEnumerable<(string col, int bytesPerChar, int maxChars, bool clampable)> p)
            => p.Sum(x => x.bytesPerChar * x.maxChars);

        var currentBytes = EstimateBytes(parts);
        if (currentBytes <= ConservativeMaxKeyBytes)
            return null; // shouldn't have thrown ORA-01450; don't rewrite.

        // Clamp only string columns, proportionally, but preserve at least 10 chars each.
        var clampables = parts.Where(p => p.clampable).ToList();
        if (clampables.Count == 0) return null;

        var fixedParts = new List<(string col, int bytesPerChar, int maxChars, bool clampable)>();
        fixedParts.AddRange(parts);

        // Simple greedy reduction: reduce largest column lengths until under limit.
        while (EstimateBytes(fixedParts) > ConservativeMaxKeyBytes)
        {
            var max = fixedParts
                .Select((p, i) => (p, i))
                .Where(x => x.p.clampable && x.p.maxChars > 10)
                .OrderByDescending(x => x.p.bytesPerChar * x.p.maxChars)
                .FirstOrDefault();

            if (max.p.col is null)
                break;

            var reduced = max.p with { maxChars = Math.Max(10, max.p.maxChars - 50) };
            fixedParts[max.i] = reduced;
        }

        if (EstimateBytes(fixedParts) > ConservativeMaxKeyBytes)
            return null;

        // Rebuild column list: SUBSTR for clamped string columns, otherwise quoted column.
        static string Q(string ident) => OracleIdent.QuoteIdent(ident);
        var newCols = new List<string>();
        foreach (var p in fixedParts)
        {
            if (p.clampable)
            {
                // Only apply SUBSTR when reduced.
                if (colInfo.TryGetValue(p.col, out var info) && info.CharLength > p.maxChars)
                    newCols.Add($"SUBSTR({Q(p.col)},1,{p.maxChars})");
                else
                    newCols.Add(Q(p.col));
            }
            else
            {
                newCols.Add(Q(p.col));
            }
        }

        var rewritten = $"CREATE INDEX {idx} ON {tableRef} ({string.Join(",", newCols)})";
        return rewritten;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.StartsWith("\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal) && s.Length >= 2)
            return s.Substring(1, s.Length - 2);
        return s;
    }

    private static async Task<Dictionary<string, (string DataType, int CharLength)>> ReadColumnInfoAsync(
        OracleConnection openOra,
        string? schema,
        string table,
        CancellationToken ct)
    {
        // ALL_TAB_COLUMNS gives CHAR_LENGTH for character types.
        var sql = @"
SELECT COLUMN_NAME, DATA_TYPE,
       NVL(CHAR_LENGTH, DATA_LENGTH) AS CHAR_LENGTH
FROM ALL_TAB_COLUMNS
WHERE TABLE_NAME = :t
  AND (:o IS NULL OR OWNER = :o)";

        await using var cmd = new OracleCommand(sql, openOra);
        cmd.BindByName = true;
        cmd.Parameters.Add(new OracleParameter("t", table.ToUpperInvariant()));
        cmd.Parameters.Add(new OracleParameter("o", (object?)schema?.ToUpperInvariant() ?? DBNull.Value));

        var dict = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var name = rdr.GetString(0);
            var dt = rdr.GetString(1);
            var len = Convert.ToInt32(rdr.GetDecimal(2));
            dict[name] = (dt, len);
        }
        return dict;
    }
}
