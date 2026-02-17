using System.Text;
using SqlToOracleMigrator.Core.Utilities;

namespace SqlToOracleMigrator.Core.Migration.ExecuteVerify;

public static class CertificateReportWriter
{
    public static async Task<string> WritePdfAsync(MigrationCertificate cert, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "ExecuteVerify_MigrationCertificate.pdf");

        var lines = new List<(string Label, string Value)>
        {
            ("RunId", cert.RunId.ToString()),
            ("Source DB", cert.SourceDatabase),
            ("Target Schema", cert.TargetSchema),
            ("Issued (UTC)", cert.IssuedAtUtc.ToString("O")),
            ("Confidence", $"{cert.Confidence}%"),
            ("Tables Verified", cert.TablesVerified.ToString()),
            ("Tables Mismatched", cert.TablesMismatched.ToString()),
            (" ", ""),
            ("Issues", cert.Issues.Count == 0 ? "No issues detected." : $"{cert.Issues.Count} issue(s)"),
        };

        if (cert.Issues.Count > 0)
        {
            foreach (var i in cert.Issues)
            {
                ct.ThrowIfCancellationRequested();
                lines.Add(("-", $"[{i.Severity}] {i.Code} | {i.Title} | {i.RecommendedAction}"));
            }
        }

        await SimplePdfWriter.WriteTextPdfAsync(path, "Migration Certificate", lines, ct);
        return path;
    }

    public static byte[] ToJsonBytes(MigrationCertificate cert)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(cert, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return Encoding.UTF8.GetBytes(json);
    }
}
