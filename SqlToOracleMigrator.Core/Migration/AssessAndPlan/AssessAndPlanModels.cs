using System.Text.Json.Serialization;

namespace SqlToOracleMigrator.Core.Migration.AssessAndPlan;

// NOTE: This file exists to provide Phase-1/Phase-2 shared types used by SchemaBuild and Assess&Plan.
// Some branches did not include the AssessAndPlan folder; this restores the required contracts.

public enum FindingSeverity
{
    Low = 0,
    Medium = 1,
    High = 2
}

public sealed class PreflightFinding
{
    public FindingSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
}

public sealed class OracleEnvironmentFingerprint
{
    public string ContainerName { get; set; } = "";
    public string? DatabaseName { get; set; }
    public string? Version { get; set; }
    public string? CharacterSet { get; set; }
    public string? NlsLanguage { get; set; }

    public List<string> MissingPrivileges { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class InventoryDeepScan
{
    public string SourceDatabase { get; set; } = "";
    public DateTimeOffset CollectedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public int TableCount { get; set; }
    public int ViewCount { get; set; }
    public int ProcedureCount { get; set; }
    public int FunctionCount { get; set; }
    public int TriggerCount { get; set; }
    public int SynonymCount { get; set; }
    public int SequenceCount { get; set; }
    public int UserDefinedTypeCount { get; set; }
    public int ForeignKeyCount { get; set; }

    public List<InventoryTableNode> Tables { get; set; } = new();
}

public sealed class InventoryTableNode
{
    public string Schema { get; set; } = "";
    public string Table { get; set; } = "";
    public long? RowCount { get; set; }
    public int ColumnCount { get; set; }
    public int IndexCount { get; set; }
    public int LobCount { get; set; }
    public int TriggerCount { get; set; }

    public int ComplexityScore { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DependsOn { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DependedBy { get; set; }
}
