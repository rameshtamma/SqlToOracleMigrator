using System.Text;

namespace SqlToOracleMigrator.Core.Utilities;

/// <summary>
/// Minimal, dependency-free PDF writer for simple text reports.
/// 
/// Why this exists:
/// - The original implementation used an iTextSharp NuGet package that may not be available in some feeds.
/// - To keep the solution buildable without external PDF dependencies, we generate a small valid PDF
///   containing a single page of text using basic PDF syntax.
/// 
/// Limitations:
/// - Single page, monospaced-ish layout, no images/tables.
/// - Suitable for "certificate" / summary reports.
/// </summary>
public static class SimplePdfWriter
{
    public static async Task WriteTextPdfAsync(
        string filePath,
        string title,
        IEnumerable<(string Label, string Value)> lines,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Build page content stream.
        // Use built-in Helvetica (no embedding needed).
        var content = new StringBuilder();
        content.AppendLine("BT");
        content.AppendLine("/F1 14 Tf");
        content.AppendLine("50 780 Td");
        content.AppendLine($"({Escape(title)}) Tj");
        content.AppendLine("0 -22 Td");
        content.AppendLine("/F1 10 Tf");

        foreach (var (label, value) in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = string.IsNullOrWhiteSpace(label)
                ? value
                : $"{label}: {value}";
            content.AppendLine($"({Escape(line)}) Tj");
            content.AppendLine("0 -14 Td");
        }

        content.AppendLine("ET");
        var contentBytes = Encoding.ASCII.GetBytes(content.ToString());

        // PDF objects
        // 1: Catalog
        // 2: Pages
        // 3: Page
        // 4: Font
        // 5: Contents

        var objects = new List<byte[]>();
        objects.Add(Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
        objects.Add(Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"));
        objects.Add(Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        objects.Add(Encoding.ASCII.GetBytes($"<< /Length {contentBytes.Length} >>\nstream\n{content.ToString()}\nendstream"));

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);

        // Header
        await WriteAsciiAsync(fs, "%PDF-1.4\n", cancellationToken);

        // Write objects and track offsets
        var offsets = new List<long> { 0 }; // xref requires object 0
        for (var i = 0; i < objects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offsets.Add(fs.Position);
            var objNum = i + 1;
            await WriteAsciiAsync(fs, $"{objNum} 0 obj\n", cancellationToken);
            await fs.WriteAsync(objects[i], cancellationToken);
            await WriteAsciiAsync(fs, "\nendobj\n", cancellationToken);
        }

        // xref
        var xrefPos = fs.Position;
        await WriteAsciiAsync(fs, "xref\n", cancellationToken);
        await WriteAsciiAsync(fs, $"0 {objects.Count + 1}\n", cancellationToken);
        await WriteAsciiAsync(fs, "0000000000 65535 f \n", cancellationToken);
        for (var i = 1; i < offsets.Count; i++)
        {
            await WriteAsciiAsync(fs, offsets[i].ToString("0000000000") + " 00000 n \n", cancellationToken);
        }

        // trailer
        await WriteAsciiAsync(fs, "trailer\n", cancellationToken);
        await WriteAsciiAsync(fs, $"<< /Size {objects.Count + 1} /Root 1 0 R >>\n", cancellationToken);
        await WriteAsciiAsync(fs, "startxref\n", cancellationToken);
        await WriteAsciiAsync(fs, xrefPos.ToString() + "\n", cancellationToken);
        await WriteAsciiAsync(fs, "%%EOF\n", cancellationToken);
    }

    private static async Task WriteAsciiAsync(Stream s, string text, CancellationToken ct)
    {
        var b = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(b, ct);
    }

    private static string Escape(string s)
    {
        // Escape PDF literal string characters.
        return s.Replace("\\", "\\\\")
                .Replace("(", "\\(")
                .Replace(")", "\\)")
                .Replace("\r", " ")
                .Replace("\n", " ");
    }
}
