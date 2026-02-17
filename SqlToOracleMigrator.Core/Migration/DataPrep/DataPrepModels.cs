using System.Text.Json.Serialization;

namespace SqlToOracleMigrator.Core.Migration.DataPrep;

public sealed class DataPrepStrategy
{
    public string SourceDatabase { get; set; } = "";
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int DefaultBatchSize { get; set; } = 50000;
    public int DefaultFallbackBatchRows { get; set; } = 1000;

    public List<TableStrategy> Tables { get; set; } = new();
}

public sealed class TableStrategy
{
    public string Schema { get; set; } = "";
    public string Table { get; set; } = "";

    public bool UseBulkCopy { get; set; } = true;
    public int? BatchSizeOverride { get; set; }

    public bool RequiresXmlStaging { get; set; }
    public bool RequiresSpatialStaging { get; set; }

    /// <summary>
    /// If true, stage 7 will relax NOT NULL on the main XML/SPATIAL column to allow load into staging columns.
    /// Stage 9 re-enforces.
    /// </summary>
    public bool RelaxNotNullOnStagedColumns { get; set; } = true;

    /// <summary>
    /// If true, apply "empty-string/zero/min-date" defaults when source violates NOT NULL.
    /// (This prevents ORA-01400 during Stage 8.)
    /// </summary>
    public bool ApplyNotNullDefaultPolicy { get; set; } = true;

    public TableSampleSummary Sample { get; set; } = new();
}

public sealed class TableSampleSummary
{
    public int SampledRows { get; set; }

    public int NotNullViolations { get; set; }
    public int DateParseWarnings { get; set; }

    public Dictionary<string, int> MaxStringLengthByColumn { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? MinDate { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? MaxDate { get; set; }

    public List<string> Notes { get; set; } = new();
}

public enum DataStrategyRisk
{
    Low,
    Medium,
    High
}

public sealed class DataPrepReport
{
    public DataPrepStrategy Strategy { get; set; } = new();

    public DataStrategyRisk Risk { get; set; } = DataStrategyRisk.Low;
    public int Confidence { get; set; } = 100;

    public List<DataPrepFinding> Findings { get; set; } = new();
}

public sealed class DataPrepFinding
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Severity { get; set; } = "Low"; // Low/Medium/High
    public string RecommendedAction { get; set; } = "";
}
