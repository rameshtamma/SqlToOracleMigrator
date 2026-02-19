using System.Text;

namespace SqlToOracleMigrator.Core.Reporting;

/// <summary>
/// Appends (or updates) a post-migration validation section inside an existing RunSummary.html.
/// This is intentionally lightweight and does NOT regenerate the whole RunSummary document,
/// so it cannot accidentally wipe earlier content.
/// </summary>
public static class RunSummaryHtmlAppender
{
    private const string StartMarker = "<!--POST_MIGRATION_VALIDATION_START-->";
    private const string EndMarker = "<!--POST_MIGRATION_VALIDATION_END-->";

    public static void AppendOrUpdatePostMigrationValidation(string runSummaryHtmlPath, string validationHtmlFile, string validationJsonFile, string summaryLine)
    {
        if (string.IsNullOrWhiteSpace(runSummaryHtmlPath) || !File.Exists(runSummaryHtmlPath)) return;

        var html = File.ReadAllText(runSummaryHtmlPath);

        var section = BuildSection(validationHtmlFile, validationJsonFile, summaryLine);

        if (html.Contains(StartMarker, StringComparison.OrdinalIgnoreCase) && html.Contains(EndMarker, StringComparison.OrdinalIgnoreCase))
        {
            var start = html.IndexOf(StartMarker, StringComparison.OrdinalIgnoreCase);
            var end = html.IndexOf(EndMarker, StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && end > start)
            {
                end += EndMarker.Length;
                html = html[..start] + section + html[end..];
            }
        }
        else
        {
            // Insert before </body> if possible; otherwise append.
            var insertAt = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (insertAt >= 0)
                html = html.Insert(insertAt, "\n" + section + "\n");
            else
                html += "\n" + section + "\n";
        }

        File.WriteAllText(runSummaryHtmlPath, html);
    }

    private static string BuildSection(string validationHtmlFile, string validationJsonFile, string summaryLine)
    {
        var sb = new StringBuilder();
        sb.AppendLine(StartMarker);
        sb.AppendLine("<hr/>");
        sb.AppendLine("<h3>Post-migration validation</h3>");
        if (!string.IsNullOrWhiteSpace(summaryLine))
            sb.AppendLine($"<div><b>Summary:</b> {System.Net.WebUtility.HtmlEncode(summaryLine)}</div>");
        sb.AppendLine("<ul>");
        if (!string.IsNullOrWhiteSpace(validationHtmlFile))
            sb.AppendLine($"  <li><a href='{System.Net.WebUtility.HtmlEncode(validationHtmlFile)}'>Validation report (HTML)</a></li>");
        if (!string.IsNullOrWhiteSpace(validationJsonFile))
            sb.AppendLine($"  <li><a href='{System.Net.WebUtility.HtmlEncode(validationJsonFile)}'>Validation report (JSON)</a></li>");
        sb.AppendLine("</ul>");
        sb.AppendLine(EndMarker);
        return sb.ToString();
    }
}
