using System.Text;
using System.Text.Json;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    internal sealed record StageExecutionReport(
        string RunId,
        string Stage,
        string Phase,
        string Status,
        DateTimeOffset StartedUtc,
        DateTimeOffset? CompletedUtc,
        double DurationSeconds,
        string DurationHuman,
        string SourceConnection,
        string SourceDatabase,
        string TargetConnection,
        string TargetSchema,
        int ErrorCount,
        IReadOnlyList<StageError> Errors);

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:0.###}s";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }

    private void WriteStageExecutionReport(MigrationContext ctx, StageExecutionReport report)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ctx.RunDir)) return;
            Directory.CreateDirectory(ctx.RunDir);

            var safeStage = string.Concat(report.Stage.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var txtPath = Path.Combine(ctx.RunDir, $"{safeStage}_report.txt");
            var jsonPath = Path.Combine(ctx.RunDir, $"{safeStage}_report.json");

            var sb = new StringBuilder();
            sb.AppendLine($"RunId: {report.RunId}");
            sb.AppendLine($"Stage: {report.Stage}");
            sb.AppendLine($"Phase: {report.Phase}");
            sb.AppendLine($"Status: {report.Status}");
            sb.AppendLine($"StartedUtc: {report.StartedUtc:O}");
            sb.AppendLine($"CompletedUtc: {(report.CompletedUtc.HasValue ? report.CompletedUtc.Value.ToString("O") : string.Empty)}");
            sb.AppendLine($"DurationSeconds: {report.DurationSeconds:0.###}");
            sb.AppendLine($"DurationHuman: {report.DurationHuman}");
            sb.AppendLine($"Source: {report.SourceConnection} / {report.SourceDatabase}");
            sb.AppendLine($"Target: {report.TargetConnection} / {report.TargetSchema}");
            sb.AppendLine($"ErrorCount: {report.ErrorCount}");

            if (report.Errors is not null && report.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                foreach (var e in report.Errors)
                    sb.AppendLine($"- {e.Schema}.{e.Object}: {e.ErrorType}: {e.Message}");
            }

            File.WriteAllText(txtPath, sb.ToString(), Encoding.UTF8);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Never fail the migration due to report writing.
            _logger.Warn($"Failed to write stage execution report: {ex.Message}");
        }
    }

    private void UpdateRunSummaryHtml(MigrationContext ctx)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ctx.RunDir)) return;
            Directory.CreateDirectory(ctx.RunDir);

            var htmlPath = Path.Combine(ctx.RunDir, "RunSummary.html");
            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html><html><head><meta charset='utf-8'>");
            sb.AppendLine("<title>SqlToOracleMigrator - Run Summary</title>");
            sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:16px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ddd;padding:8px}th{background:#f3f3f3;text-align:left} .ok{color:#0a7} .fail{color:#c00} .warn{color:#c60}</style>");
            sb.AppendLine("</head><body>");
            sb.AppendLine($"<h2>Run Summary</h2>");
            sb.AppendLine($"<div><b>RunId:</b> {ctx.Summary.RunId}</div>");
            sb.AppendLine($"<div><b>Source:</b> {ctx.Summary.SourceConnection} / {ctx.Summary.SourceDatabase}</div>");
            sb.AppendLine($"<div><b>Target:</b> {ctx.Summary.TargetConnection} / {ctx.Summary.TargetSchema}</div>");
            sb.AppendLine("<br/>");

            sb.AppendLine("<table><thead><tr><th>Stage</th><th>Phase</th><th>Status</th><th>Duration</th><th>Links</th></tr></thead><tbody>");

            foreach (var kv in ctx.StageReports.OrderBy(k => (int)k.Key))
            {
                var rep = kv.Value;
                var cls = rep.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ? "ok" :
                          rep.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? "fail" : "warn";

                var safeStage = string.Concat(rep.Stage.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                var repTxt = $"{safeStage}_report.txt";
                var repJson = $"{safeStage}_report.json";
                var errTxt = $"{safeStage}_errors.txt";
                var errJson = $"{safeStage}_errors.json";

                sb.AppendLine("<tr>");
                sb.AppendLine($"<td>{rep.Stage}</td>");
                sb.AppendLine($"<td>{rep.Phase}</td>");
                sb.AppendLine($"<td class='{cls}'>{rep.Status}</td>");
                sb.AppendLine($"<td>{rep.DurationHuman}</td>");

                var links = new List<string>
                {
                    $"<a href='{repTxt}'>report.txt</a>",
                    $"<a href='{repJson}'>report.json</a>"
                };
                if (File.Exists(Path.Combine(ctx.RunDir, errTxt))) links.Add($"<a href='{errTxt}'>errors.txt</a>");
                if (File.Exists(Path.Combine(ctx.RunDir, errJson))) links.Add($"<a href='{errJson}'>errors.json</a>");

                // Common artifacts
                var ddlSql = Path.Combine(ctx.RunDir, "SchemaBuild_DDL.sql");
                var ddlZip = Path.Combine(ctx.RunDir, "SchemaBuild_DDL.zip");
                if (File.Exists(ddlSql)) links.Add("<a href='SchemaBuild_DDL.sql'>SchemaBuild_DDL.sql</a>");
                if (File.Exists(ddlZip)) links.Add("<a href='SchemaBuild_DDL.zip'>SchemaBuild_DDL.zip</a>");

                sb.AppendLine($"<td>{string.Join(" | ", links)}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody></table>");
            sb.AppendLine("</body></html>");

            File.WriteAllText(htmlPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to update RunSummary.html: {ex.Message}");
        }
    }
}
