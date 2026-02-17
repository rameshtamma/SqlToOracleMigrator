using NUnit.Framework;
using SqlToOracleMigrator.Core.Oracle;

namespace SqlToOracleMigrator.Tests;

[TestFixture]
public sealed class SchemaBuildTests
{
    [Test]
    public void SchemaBuildDdlBundle_ParseCombined_SplitsStatementsAndCapturesMetadata()
    {
        var combined = @"
-- TABLE HR.EMP
CREATE TABLE HR.EMP (ID NUMBER)

-- INDEX HR.IX_EMP_ID
CREATE INDEX HR.IX_EMP_ID ON HR.EMP(ID)
";

        var b = SchemaBuildDdlBundle.ParseCombined(combined);
        Assert.That(b.Statements.Count, Is.EqualTo(2));

        Assert.That(b.Statements[0].ObjectType, Is.EqualTo("TABLE"));
        Assert.That(b.Statements[0].Schema, Is.EqualTo("HR"));
        Assert.That(b.Statements[0].ObjectName, Is.EqualTo("EMP"));
        Assert.That(b.Statements[0].Sql, Does.StartWith("CREATE TABLE"));

        Assert.That(b.Statements[1].ObjectType, Is.EqualTo("INDEX"));
        Assert.That(b.Statements[1].ObjectName, Is.EqualTo("IX_EMP_ID"));
    }

    [Test]
    public void OracleErrorCatalog_MapsKnownCodes_Stub()
    {
        // Unit-test stubs: OracleException does not expose public constructors.
        // Replace with an integration test that captures real OracleException instances.
        Assert.Pass("Stub");
    }
}
