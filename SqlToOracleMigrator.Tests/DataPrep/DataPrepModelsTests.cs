using NUnit.Framework;
using SqlToOracleMigrator.Core.Migration.DataPrep;
using System.Text.Json;

namespace SqlToOracleMigrator.Tests.DataPrep;

[TestFixture]
public sealed class DataPrepModelsTests
{
    [Test]
    public void DataPrepStrategy_RoundTripJson_Works()
    {
        var s = new DataPrepStrategy
        {
            SourceDatabase = "AdventureWorks",
            DefaultBatchSize = 50000,
            Tables =
            {
                new TableStrategy
                {
                    Schema = "dbo",
                    Table = "Person",
                    UseBulkCopy = true,
                    RequiresXmlStaging = true,
                    RequiresSpatialStaging = false,
                    Sample = new TableSampleSummary { SampledRows = 3, NotNullViolations = 1 }
                }
            }
        };

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<DataPrepStrategy>(json);

        Assert.That(back, Is.Not.Null);
        Assert.That(back!.SourceDatabase, Is.EqualTo("AdventureWorks"));
        Assert.That(back.Tables.Count, Is.EqualTo(1));
        Assert.That(back.Tables[0].RequiresXmlStaging, Is.True);
    }

    [Test]
    public void DataPrepReport_Defaults_AreSane()
    {
        var r = new DataPrepReport();
        Assert.That(r.Confidence, Is.EqualTo(100));
        Assert.That(r.Risk, Is.EqualTo(DataStrategyRisk.Low));
    }
}
