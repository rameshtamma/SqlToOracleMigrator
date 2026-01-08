using System.Text.Json.Serialization;

namespace SqlToOracleMigrator.Core.Tracking;

public sealed class ToolMigRunInfo
{
    public Guid RunId { get; set; }
    public string SourceDatabase { get; set; } = "";
    public string? TargetDatabase { get; set; }
    public int Version { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string Status { get; set; } = "Running"; // Running|Completed|Failed
    public string? Notes { get; set; }

    /// <summary>
    /// JSON-serialized request/settings captured when the run was created.
    /// Used for diagnostics and best-effort resume validation.
    /// </summary>
    public string? RequestJson { get; set; }
}

public sealed class ToolMigStageInfo
{
    public Guid RunId { get; set; }
    public string Stage { get; set; } = "";
    public string Status { get; set; } = "NotStarted"; // NotStarted|InProgress|Completed|Failed|Skipped
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int ErrorCount { get; set; }
    public string? Message { get; set; }
}

public sealed class ToolMigObjectInfo
{
    public Guid RunId { get; set; }
    public string Stage { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string ObjectType { get; set; } = "TABLE";
    public string Status { get; set; } = "NotStarted"; // NotStarted|InProgress|Completed|Failed|Skipped
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
