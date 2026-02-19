using System.Text;

namespace SqlToOracleMigrator.Core.Oracle;

public sealed class SchemaBuildDdlStatement
{
    public string Sql { get; set; } = "";
    public string? Schema { get; set; }
    public string? ObjectName { get; set; }
    public string? ObjectType { get; set; }
}

/// <summary>
/// Bundle of DDL statements with lightweight metadata.
/// Stored as a single combined SQL file in RunArtifacts.
/// </summary>
public sealed class SchemaBuildDdlBundle
{
    public List<SchemaBuildDdlStatement> Statements { get; } = new();

    public string CombinedSql
        => string.Join("\n\n", Statements.Select(s => s.Sql));

    public static SchemaBuildDdlBundle ParseCombined(string combined)
    {
        var b = new SchemaBuildDdlBundle();
        if (string.IsNullOrWhiteSpace(combined)) return b;

        // Expected format created by SchemaBuildDdlComposer:
        // -- OBJTYPE SCHEMA.OBJECT
        // <statement>;
        var lines = combined.Split('\n');
        var cur = new StringBuilder();
        string? schema = null, name = null, type = null;

        void Flush()
        {
            var sql = cur.ToString().Trim();
            if (sql.Length == 0) return;
            b.Statements.Add(new SchemaBuildDdlStatement { Sql = sql, Schema = schema, ObjectName = name, ObjectType = type });
            cur.Clear();
        }

        // Header detection guardrails:
        // - Only treat a comment line as a new-object header if it begins with a known object token
        //   (TABLE/VIEW/PROCEDURE/...) OR if it uses the explicit marker "-- @OBJ".
        // - Do NOT treat ordinary comments inside DDL as headers (root cause of false ORA-06550/PLS-00103).
        static bool IsHeader(string line, out string header)
        {
            header = string.Empty;
            if (!line.StartsWith("-- ")) return false;
            var h = line.Substring(3).Trim();
            if (h.StartsWith("@OBJ", StringComparison.OrdinalIgnoreCase))
            {
                header = h.Substring(4).Trim();
                return true;
            }
            // legacy header format: "TYPE SCHEMA.NAME"
            var first = h.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var token = first.ToUpperInvariant();
            var known = token is "TABLE" or "VIEW" or "PROCEDURE" or "FUNCTION" or "TRIGGER" or "INDEX" or "CONSTRAINT" or "TYPE" or "SEQUENCE" or "SYNONYM" or "PACKAGE" or "DDL";
            if (!known) return false;
            header = h;
            return true;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (IsHeader(line, out var hdr))
            {
                // header starts a new statement
                Flush();
                var parts = hdr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                type = parts.Length > 0 ? parts[0].Trim() : null;
                schema = null; name = null;
                if (parts.Length == 2)
                {
                    var obj = parts[1].Trim();
                    var dot = obj.IndexOf('.');
                    if (dot > 0)
                    {
                        schema = obj.Substring(0, dot);
                        name = obj.Substring(dot + 1);
                    }
                    else
                    {
                        name = obj;
                    }
                }
                continue;
            }
            cur.AppendLine(line);
        }
        Flush();
        return b;
    }
}
