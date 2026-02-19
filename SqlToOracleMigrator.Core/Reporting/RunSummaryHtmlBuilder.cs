using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SqlToOracleMigrator.Core.Reporting
{
    internal static class RunSummaryHtmlBuilder
    {
        private sealed class StageReportDto
        {
            public string? RunId { get; set; }
            public string? Stage { get; set; }
            public string? Phase { get; set; }
            public string? Status { get; set; }
            public DateTimeOffset? StartedUtc { get; set; }
            public DateTimeOffset? CompletedUtc { get; set; }
            public double? DurationSeconds { get; set; }
            public string? DurationHuman { get; set; }
            public string? SourceConnection { get; set; }
            public string? SourceDatabase { get; set; }
            public string? TargetConnection { get; set; }
            public string? TargetSchema { get; set; }
            public int? ErrorCount { get; set; }
        }

        /// <summary>
        /// Rebuilds RunSummary.html from the run directory contents.
        ///
        /// Output format intentionally matches the "classic" RunSummary.html style:
        /// - Header: Run Summary
        /// - RunId/Source/Target lines
        /// - Stages table with: Stage | Phase | Status | Duration | Links
        ///
        /// Additionally appends a Post-migration validation section (links + summary),
        /// without removing the stages section.
        /// </summary>
        public static void Rebuild(string runDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
                    return;

                var existingPath = Path.Combine(runDir, "RunSummary.html");

                // Preserve any existing post-migration section (if present) in case the current folder doesn't contain validation artifacts yet.
                string? preservedPostValidationSection = null;
                if (File.Exists(existingPath))
                {
                    var existing = File.ReadAllText(existingPath);
                    preservedPostValidationSection = ExtractSection(existing,
                        "<!--POST_MIGRATION_VALIDATION_START-->",
                        "<!--POST_MIGRATION_VALIDATION_END-->");
                }

                var stageReports = LoadStageReports(runDir)
                    .OrderBy(r => r.StartedUtc ?? DateTimeOffset.MaxValue)
                    .ThenBy(r => r.Stage ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var runId = stageReports.Select(r => r.RunId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                            ?? TryReadRunIdFromJson(Path.Combine(runDir, "run_summary.json"));

                var srcConn = stageReports.Select(r => r.SourceConnection).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                var srcDb = stageReports.Select(r => r.SourceDatabase).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                var tgtConn = stageReports.Select(r => r.TargetConnection).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                var tgtSchema = stageReports.Select(r => r.TargetSchema).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

                var sb = new StringBuilder();
                sb.AppendLine("<!doctype html><html><head><meta charset='utf-8'>");
                sb.AppendLine("<title>SqlToOracleMigrator - Run Summary</title>");
                sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:16px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ddd;padding:8px}th{background:#f3f3f3;text-align:left} .ok{color:#0a7} .fail{color:#c00} .warn{color:#c60}</style>");
                sb.AppendLine("</head><body>");

                sb.AppendLine("<h2>Run Summary</h2>");
                if (!string.IsNullOrWhiteSpace(runId))
                    sb.AppendLine($"<div><b>RunId:</b> {Html(runId!)}</div>");
                if (!string.IsNullOrWhiteSpace(srcConn) || !string.IsNullOrWhiteSpace(srcDb))
                    sb.AppendLine($"<div><b>Source:</b> {Html(srcConn)} / {Html(srcDb)}</div>");
                if (!string.IsNullOrWhiteSpace(tgtConn) || !string.IsNullOrWhiteSpace(tgtSchema))
                    sb.AppendLine($"<div><b>Target:</b> {Html(tgtConn)} / {Html(tgtSchema)}</div>");

                sb.AppendLine("<br/>");

                sb.AppendLine("<table><thead><tr><th>Stage</th><th>Phase</th><th>Status</th><th>Duration</th><th>Links</th></tr></thead><tbody>");

                foreach (var r in stageReports)
                {
                    var stage = r.Stage ?? "Unknown";
                    var phase = r.Phase ?? "";
                    var status = r.Status ?? (r.ErrorCount.GetValueOrDefault() > 0 ? "Failed" : "Completed");
                    var dur = !string.IsNullOrWhiteSpace(r.DurationHuman) ? r.DurationHuman :
                              r.DurationSeconds.HasValue ? $"{r.DurationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)}s" : "";

                    var statusCss = status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ? "ok"
                                  : status.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "warn"
                                  : status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? "fail" : "";

                    var links = new List<string>();
                    var reportTxt = $"{stage}_report.txt";
                    var reportJson = $"{stage}_report.json";
                    var errTxt = $"{stage}_errors.txt";
                    var errJson = $"{stage}_errors.json";

                    if (File.Exists(Path.Combine(runDir, reportTxt))) links.Add($"<a href='{HtmlAttr(reportTxt)}'>report.txt</a>");
                    if (File.Exists(Path.Combine(runDir, reportJson))) links.Add($"<a href='{HtmlAttr(reportJson)}'>report.json</a>");
                    if (File.Exists(Path.Combine(runDir, errTxt))) links.Add($"<a href='{HtmlAttr(errTxt)}'>errors.txt</a>");
                    if (File.Exists(Path.Combine(runDir, errJson))) links.Add($"<a href='{HtmlAttr(errJson)}'>errors.json</a>");

                    // Classic report expects the SchemaBuild_DDL artifacts to be available as links.
                    if (File.Exists(Path.Combine(runDir, "SchemaBuild_DDL.sql"))) links.Add("<a href='SchemaBuild_DDL.sql'>SchemaBuild_DDL.sql</a>");
                    if (File.Exists(Path.Combine(runDir, "SchemaBuild_DDL.zip"))) links.Add("<a href='SchemaBuild_DDL.zip'>SchemaBuild_DDL.zip</a>");

                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td>{Html(stage)}</td>");
                    sb.AppendLine($"<td>{Html(phase)}</td>");
                    sb.AppendLine($"<td class='{statusCss}'>{Html(status)}</td>");
                    sb.AppendLine($"<td>{Html(dur)}</td>");
                    sb.AppendLine($"<td>{string.Join(" | ", links)}</td>");
                    sb.AppendLine("</tr>");
                }

                sb.AppendLine("</tbody></table>");

                // Post-migration validation: regenerate from current artifacts when possible; otherwise preserve prior section.
                var computedPost = BuildPostMigrationSection(runDir);
                if (!string.IsNullOrWhiteSpace(computedPost))
                {
                    sb.AppendLine();
                    sb.AppendLine(computedPost);
                }
                else if (!string.IsNullOrWhiteSpace(preservedPostValidationSection))
                {
                    sb.AppendLine();
                    sb.AppendLine(preservedPostValidationSection);
                }

                sb.AppendLine("</body></html>");

                File.WriteAllText(existingPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Never throw from summary generation.
            }
        }

        private static List<StageReportDto> LoadStageReports(string runDir)
        {
            var list = new List<StageReportDto>();
            foreach (var path in Directory.EnumerateFiles(runDir, "*_report.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    json = json.TrimStart('\uFEFF', '\u200B');
                    var dto = JsonSerializer.Deserialize<StageReportDto>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (dto != null)
                        list.Add(dto);
                }
                catch
                {
                    // ignore individual files
                }
            }
            return list;
        }

        private static string? TryReadRunIdFromJson(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path).TrimStart('\uFEFF', '\u200B');
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("RunId", out var p) && p.ValueKind == JsonValueKind.String)
                    return p.GetString();
            }
            catch
            {
                // ignore
            }
            return null;
        }

        private static string ExtractSection(string html, string startMarker, string endMarker)
        {
            try
            {
                var s = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
                var e = html.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
                if (s >= 0 && e > s)
                {
                    e += endMarker.Length;
                    return html.Substring(s, e - s);
                }
            }
            catch
            {
                // ignore
            }
            return "";
        }

        private static string BuildPostMigrationSection(string runDir)
        {
            try
            {
                var htmlPath = Directory.EnumerateFiles(runDir, "PostMigrationValidation_*.html", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                var jsonPath = Directory.EnumerateFiles(runDir, "PostMigrationValidation_*.json", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (htmlPath == null && jsonPath == null)
                    return "";

                int? err = null, warn = null, srcObj = null, tgtObj = null;
                if (jsonPath != null && File.Exists(jsonPath))
                {
                    var txt = File.ReadAllText(jsonPath).TrimStart('\uFEFF', '\u200B');
                    using var doc = JsonDocument.Parse(txt);
                    if (doc.RootElement.TryGetProperty("Summary", out var s) && s.ValueKind == JsonValueKind.Object)
                    {
                        if (s.TryGetProperty("ErrorCount", out var p) && p.ValueKind == JsonValueKind.Number) err = p.GetInt32();
                        if (s.TryGetProperty("WarnCount", out var w) && w.ValueKind == JsonValueKind.Number) warn = w.GetInt32();
                        if (s.TryGetProperty("SourceObjectCount", out var so) && so.ValueKind == JsonValueKind.Number) srcObj = so.GetInt32();
                        if (s.TryGetProperty("TargetObjectCount", out var to) && to.ValueKind == JsonValueKind.Number) tgtObj = to.GetInt32();
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("<!--POST_MIGRATION_VALIDATION_START-->");
                sb.AppendLine("<br/>");
                sb.AppendLine("<h3>Post-migration validation</h3>");
                if (srcObj.HasValue || tgtObj.HasValue)
                    sb.AppendLine($"<div><b>Objects:</b> Source={srcObj?.ToString() ?? ""}, Target={tgtObj?.ToString() ?? ""}</div>");
                if (err.HasValue || warn.HasValue)
                    sb.AppendLine($"<div><b>Issues:</b> Errors={err?.ToString() ?? "0"}, Warnings={warn?.ToString() ?? "0"}</div>");

                sb.Append("<div style='margin-top:6px'>");
                if (htmlPath != null)
                    sb.Append($"<a href='{HtmlAttr(Path.GetFileName(htmlPath))}'>PostMigrationValidation (HTML)</a>");
                if (jsonPath != null)
                {
                    if (htmlPath != null) sb.Append(" | ");
                    sb.Append($"<a href='{HtmlAttr(Path.GetFileName(jsonPath))}'>PostMigrationValidation (JSON)</a>");
                }
                sb.AppendLine("</div>");
                sb.AppendLine("<!--POST_MIGRATION_VALIDATION_END-->");
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string Html(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private static string HtmlAttr(string s) => Html(s).Replace("'", "&#39;");
    }
}
