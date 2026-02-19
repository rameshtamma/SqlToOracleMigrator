using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using System.Text;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Generates/updates a single HTML page that links all stage reports and error files for the run.
    /// Best-effort: never throws.
    /// </summary>
    internal void UpdateRunIndexHtml(string runDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
                return;

            var reportTxt = Directory.GetFiles(runDir, "*_report.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var reportJson = Directory.GetFiles(runDir, "*_report.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var errorTxt = Directory.GetFiles(runDir, "*_errors.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var errorJson = Directory.GetFiles(runDir, "*_errors.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

            static string StagePrefix(string path)
            {
                var n = Path.GetFileName(path);
                if (n.EndsWith("_report.txt", StringComparison.OrdinalIgnoreCase)) return n[..^"_report.txt".Length];
                if (n.EndsWith("_report.json", StringComparison.OrdinalIgnoreCase)) return n[..^"_report.json".Length];
                if (n.EndsWith("_errors.txt", StringComparison.OrdinalIgnoreCase)) return n[..^"_errors.txt".Length];
                if (n.EndsWith("_errors.json", StringComparison.OrdinalIgnoreCase)) return n[..^"_errors.json".Length];
                return Path.GetFileNameWithoutExtension(n);
            }

            var stages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in reportTxt) stages.Add(StagePrefix(f));
            foreach (var f in reportJson) stages.Add(StagePrefix(f));
            foreach (var f in errorTxt) stages.Add(StagePrefix(f));
            foreach (var f in errorJson) stages.Add(StagePrefix(f));

            static string Rel(string p) => Path.GetFileName(p);

            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html><head><meta charset='utf-8'/>" );
            sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1'/>" );
            sb.AppendLine("<title>SqlToOracleMigrator - Run Summary</title>");
            sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:16px;} table{border-collapse:collapse;width:100%;} th,td{border:1px solid #ddd;padding:8px;} th{background:#f5f5f5;text-align:left;} code{background:#f8f8f8;padding:2px 4px;border-radius:4px;}</style>");
            sb.AppendLine("</head><body>");
            sb.AppendLine("<h2>Migration Run Summary</h2>");
            sb.AppendLine("<p>This page links all stage reports and error files produced in this run folder.</p>");

            var ddlSql = Path.Combine(runDir, "SchemaBuild_DDL.sql");
            var ddlZip = Path.Combine(runDir, "SchemaBuild_DDL.zip");
            if (File.Exists(ddlSql) || File.Exists(ddlZip))
            {
                sb.AppendLine("<h3>Key Artifacts</h3><ul>");
                if (File.Exists(ddlSql)) sb.AppendLine($"<li><a href='{Rel(ddlSql)}'>SchemaBuild_DDL.sql</a></li>");
                if (File.Exists(ddlZip)) sb.AppendLine($"<li><a href='{Rel(ddlZip)}'>SchemaBuild_DDL.zip</a></li>");
                sb.AppendLine("</ul>");
            }

            sb.AppendLine("<h3>Stages</h3>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Stage</th><th>Report</th><th>Errors</th></tr>");

            foreach (var st in stages)
            {
                var rt = reportTxt.FirstOrDefault(p => StagePrefix(p).Equals(st, StringComparison.OrdinalIgnoreCase));
                var rj = reportJson.FirstOrDefault(p => StagePrefix(p).Equals(st, StringComparison.OrdinalIgnoreCase));
                var et = errorTxt.FirstOrDefault(p => StagePrefix(p).Equals(st, StringComparison.OrdinalIgnoreCase));
                var ej = errorJson.FirstOrDefault(p => StagePrefix(p).Equals(st, StringComparison.OrdinalIgnoreCase));

                sb.AppendLine("<tr>");
                sb.AppendLine($"<td><code>{System.Net.WebUtility.HtmlEncode(st)}</code></td>");

                sb.Append("<td>");
                if (rt is not null) sb.Append($"<a href='{Rel(rt)}'>txt</a> ");
                if (rj is not null) sb.Append($"<a href='{Rel(rj)}'>json</a>");
                if (rt is null && rj is null) sb.Append("-");
                sb.AppendLine("</td>");

                sb.Append("<td>");
                if (et is not null) sb.Append($"<a href='{Rel(et)}'>txt</a> ");
                if (ej is not null) sb.Append($"<a href='{Rel(ej)}'>json</a>");
                if (et is null && ej is null) sb.Append("-");
                sb.AppendLine("</td>");

                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</table>");
            sb.AppendLine("</body></html>");

            File.WriteAllText(Path.Combine(runDir, "RunSummary.html"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try { _logger?.Warn($"Failed to generate RunSummary.html: {ex.Message}"); } catch { }
        }
    }

    internal sealed record MissingTargetTable(string TargetSchema, string Table);

    /// <summary>
    /// Returns any tables that are expected (based on discovery) but missing in the target after schema deployment.
    /// Best-effort: never throws.
    /// </summary>
    internal async Task<List<MissingTargetTable>> FindMissingTargetTablesAsync(OracleConnection openOra, MigrationContext ctx, CancellationToken ct)
    {
        var missing = new List<MissingTargetTable>();
        try
        {
            foreach (var t in ctx.Tables)
            {
                ct.ThrowIfCancellationRequested();
                var targetSchema = ctx.GetTargetSchema(t.Schema);
                var exists = await OracleTableExistsAsync(openOra, targetSchema, t.Table, ct);
                if (!exists)
                    missing.Add(new MissingTargetTable(targetSchema, t.Table));
            }
        }
        catch (Exception ex)
        {
            try { _logger?.Warn($"FindMissingTargetTablesAsync failed (best-effort): {ex.Message}"); } catch { }
        }
        return missing;
    }

    private static async Task<bool> OracleTableExistsAsync(OracleConnection conn, string schema, string table, CancellationToken ct)
    {
        var ownerRaw = (schema ?? string.Empty).Trim().Trim('"');
        var tableRaw = (table ?? string.Empty).Trim().Trim('"');
        var ownerUpper = ownerRaw.ToUpperInvariant();
        var tableUpper = tableRaw.ToUpperInvariant();

        await using var cmd = conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = @"SELECT COUNT(*) FROM all_tables WHERE (owner = :p_owner_raw OR owner = :p_owner_upper) AND (table_name = :p_table_raw OR table_name = :p_table_upper)";
        cmd.Parameters.Add(new OracleParameter("p_owner_raw", OracleDbType.Varchar2, ownerRaw, System.Data.ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("p_owner_upper", OracleDbType.Varchar2, ownerUpper, System.Data.ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("p_table_raw", OracleDbType.Varchar2, tableRaw, System.Data.ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("p_table_upper", OracleDbType.Varchar2, tableUpper, System.Data.ParameterDirection.Input));

        var v = await cmd.ExecuteScalarAsync(ct);
        var n = v is null or DBNull ? 0 : Convert.ToInt32(v);
        return n > 0;
    }
}
