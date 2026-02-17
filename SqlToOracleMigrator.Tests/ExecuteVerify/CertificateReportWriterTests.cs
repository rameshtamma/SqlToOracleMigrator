using NUnit.Framework;
using SqlToOracleMigrator.Core.Migration.ExecuteVerify;

namespace SqlToOracleMigrator.Tests.ExecuteVerify;

[TestFixture]
public class CertificateReportWriterTests
{
    [Test]
    public void ToJsonBytes_ContainsConfidence()
    {
        var cert = new MigrationCertificate
        {
            RunId = Guid.NewGuid(),
            SourceDatabase = "AdventureWorks",
            TargetSchema = "AW",
            Confidence = 92,
            TablesVerified = 10,
            TablesMismatched = 1
        };

        var bytes = CertificateReportWriter.ToJsonBytes(cert);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.That(json, Does.Contain("\"Confidence\": 92"));
        Assert.That(json, Does.Contain("\"SourceDatabase\": \"AdventureWorks\""));
    }

    [Test]
    public async Task WritePdfAsync_CreatesFile()
    {
        var cert = new MigrationCertificate
        {
            RunId = Guid.NewGuid(),
            SourceDatabase = "Db",
            TargetSchema = "SCHEMA",
            Confidence = 100
        };

        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cert_out");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        var path = await CertificateReportWriter.WritePdfAsync(cert, dir, CancellationToken.None);

        Assert.That(File.Exists(path), Is.True);
        var len = new FileInfo(path).Length;
        Assert.That(len, Is.GreaterThan(100));
    }
}
