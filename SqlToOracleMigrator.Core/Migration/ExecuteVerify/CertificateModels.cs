namespace SqlToOracleMigrator.Core.Migration.ExecuteVerify;

public enum CertificateSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed class VerificationIssue
{
    public CertificateSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
}

public sealed class MigrationCertificate
{
    public Guid RunId { get; set; }
    public string SourceDatabase { get; set; } = "";
    public string TargetSchema { get; set; } = "";
    public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public int TablesVerified { get; set; }
    public int TablesMismatched { get; set; }

    public int Confidence { get; set; } = 100;

    public List<VerificationIssue> Issues { get; set; } = new();
}
