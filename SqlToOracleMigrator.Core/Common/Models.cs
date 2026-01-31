using System.Text.Json.Serialization;

namespace SqlToOracleMigrator.Core;

public sealed class ConnectionDefinition
{
    // Identity / display
    public string Name { get; set; } = "";
    public DatabaseEngine Engine { get; set; } = DatabaseEngine.SqlServer;

    // Optional tags
    public string? Region { get; set; } // Dev/Uat/Prod
    public string? Notes { get; set; }
    public string? Color { get; set; } // reserved; UI only

    // Credentials
    public string? Username { get; set; }
    public bool UseWindowsAuthentication { get; set; }
    public bool SavePassword { get; set; }

    /// <summary>Base64 encoded ProtectedData payload, or null if SavePassword=false.</summary>
    public string? EncryptedPassword { get; set; }

    // Connection details
    public string Hostname { get; set; } = "";
    public int Port { get; set; } = 0;

    // SQL Server
    public string? DefaultDatabase { get; set; } // optional convenience

    // Oracle
    public string? AuthenticationType { get; set; } // display-only
    public string? ConnectionType { get; set; } // display-only
    public string? Role { get; set; } // display-only
    public bool UseSid { get; set; } = true;
    public string? Sid { get; set; }
    public string? ServiceName { get; set; }

    // Status
    public ConnectionTestStatus LastTestStatus { get; set; } = ConnectionTestStatus.Yellow;
    public DateTimeOffset? LastTestUtc { get; set; }
    public string? LastTestMessage { get; set; }

    [JsonIgnore]
    public string? RuntimePassword { get; set; } // never persisted; only in-memory for current session

    public void ValidateForTest()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("Connection Name is required.");

        if (string.IsNullOrWhiteSpace(Hostname))
            throw new InvalidOperationException("Hostname is required.");

        if (Port is < 1 or > 65535)
            throw new InvalidOperationException("Port must be between 1 and 65535.");

        if (Engine == DatabaseEngine.SqlServer)
        {
            if (!UseWindowsAuthentication)
            {
                if (string.IsNullOrWhiteSpace(Username))
                    throw new InvalidOperationException("Username is required for SQL authentication.");

                if (string.IsNullOrWhiteSpace(RuntimePassword))
                    throw new InvalidOperationException("Password is required for SQL authentication.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Username))
                throw new InvalidOperationException("Username is required for Oracle.");

            if (string.IsNullOrWhiteSpace(RuntimePassword))
                throw new InvalidOperationException("Password is required for Oracle.");

            if (UseSid)
            {
                if (string.IsNullOrWhiteSpace(Sid))
                    throw new InvalidOperationException("SID is required when SID is selected.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(ServiceName))
                    throw new InvalidOperationException("Service Name is required when Service Name is selected.");
            }
        }
    }
}

public sealed record LogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Message, string? Detail = null);

public sealed record MigrationProgress(MigrationStage Stage, string Message, double? Percent = null);

public sealed class InventoryDbSummary
{
    public string Side { get; set; } = ""; // Source/Target
    public string Engine { get; set; } = "";
    public string DatabaseOrService { get; set; } = "";
    public string DefaultSchemaOrUser { get; set; } = "";

    public double? DatabaseSizeGb { get; set; }
    public double? DataSizeGb { get; set; }
    public double? LogOrRedoSizeGb { get; set; }

    public int? SchemaCount { get; set; }
    public int? TableCount { get; set; }
    public int? ViewCount { get; set; }
    public int? ProcedureCount { get; set; }
    public int? FunctionCount { get; set; }
    public int? SequenceCount { get; set; }
    public int? SynonymCount { get; set; }
    public int? TriggerCount { get; set; }
    public int? IndexCount { get; set; }

    public DateTimeOffset? LastStatsUpdate { get; set; }

    // Lazy-load object rows
    public List<InventoryObjectSummary> Objects { get; set; } = new();
}

public sealed class InventoryObjectSummary
{
    public string Schema { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string ObjectType { get; set; } = "";

    public long? EstimatedRows { get; set; }
    public double? EstimatedSizeMb { get; set; }

    public DateTimeOffset? CreatedDate { get; set; }
    public DateTimeOffset? LastModifiedDate { get; set; }

    public int? DependsOnCount { get; set; }
    public int? DependedByCount { get; set; }

    public int ComplexityScore { get; set; } = 1;

    public string MigrationStatus { get; set; } = "Not started";
}

public sealed class MigrationRequest
{
    public required ConnectionDefinition SourceSqlConnection { get; init; }
    public required string SourceDatabase { get; init; }

    public required ConnectionDefinition TargetOracleConnection { get; init; }

    /// <summary>
    /// Legacy / single-schema mode: all objects land into this schema/user.
    /// In v6 multi-schema clone mode, this value is ignored for object placement.
    /// </summary>
    public required string TargetSchema { get; init; }

    public int DegreeOfParallelism { get; init; } = 4;

    // ----------------------------
    // v6 enhancements (kept optional with defaults)
    // ----------------------------

    /// <summary>
    /// When true, clone source schemas 1:1 into Oracle using Oracle unquoted schema naming rules (UPPERCASE).
    /// Tables in source schema X migrate into Oracle schema X.
    /// </summary>
    public bool CloneSourceSchemas { get; init; } = true;

    /// <summary>
    /// When true, attempt to auto-create missing Oracle schemas/users for cloned schemas (requires admin privileges).
    /// </summary>
    public bool AutoCreateTargetSchemas { get; init; } = true;

    /// <summary>
    /// Run DDL generation validation (parse-only, no DDL commit) before DDL deployment.
    /// </summary>
    public bool EnableDataDefValidation { get; init; } = true;

    /// <summary>
    /// Run data migration validation (dry-run inserts in a rollback transaction) before real data migration.
    /// </summary>
    public bool EnableDataValidation { get; init; } = true;

    /// <summary>
    /// Row limit for data validation per table. Default 5000. Set to 0 or negative to skip.
    /// </summary>
    public int DataValidationRowLimit { get; init; } = 5000;

    /// <summary>
    /// When true, validate full dataset (all rows) instead of top N during data validation.
    /// </summary>
    public bool ValidateFullDataset { get; init; } = false;

    // ----------------------------
    // v6.2 enhancements
    // ----------------------------

    /// <summary>
    /// When set, resumes a prior ToolMig run and skips completed stages/objects.
    /// </summary>
    public Guid? ResumeRunId { get; init; } = null;

    /// <summary>
    /// Controls whether the engine stops on first error (FailFast) or collects errors within a stage (CollectAll).
    /// </summary>
    public ErrorHandlingMode ErrorHandlingMode { get; init; } = ErrorHandlingMode.FailFast;

    // ----------------------------
    // v6.3 enhancements (2026-01)
    // ----------------------------

    /// <summary>
    /// Global requirement: on NEW runs (non-resume), override target definitions and data.
    /// When resuming a run, completed stages/objects are still respected.
    /// </summary>
    public bool OverrideTargetObjectsEachRun { get; init; } = true;

    /// <summary>
    /// When true, ensure a target Oracle PDB exists (CDB multitenant) and switch the session into it.
    /// Requires SYSDBA privileges on the target connection.
    /// </summary>
    public bool EnsureTargetPdb { get; init; } = true;

    /// <summary>
    /// When true and EnsureTargetPdb=true, if the target PDB already exists the engine will drop it
    /// (INCLUDING DATAFILES) and recreate it. This is useful when a prior run partially created the PDB
    /// and you want a clean, deterministic environment.
    /// 
    /// Guardrails: the engine will not drop protected PDBs (PDB$SEED, XEPDB*).
    /// </summary>
    public bool DropTargetPdbIfExists { get; init; } = false;

    /// <summary>
    /// Target PDB name to ensure/switch to (default Adventureworks2025).
    /// </summary>
    public string TargetPdbName { get; init; } = "Adventureworks2025";

    /// <summary>
    /// When true, create dependent objects (views/procs/functions/triggers/synonyms/sequences/types).
    /// If SQL-to-Oracle translation fails, a compilable stub may be created when CreateDependentObjectStubs=true.
    /// </summary>
    public bool CreateDependentObjects { get; init; } = true;

    /// <summary>
    /// When true, create stub Oracle objects when translation is not supported.
    /// </summary>
    public bool CreateDependentObjectStubs { get; init; } = true;

    /// <summary>
    /// When true, deploy foreign keys after data migration.
    /// </summary>
    public bool CreateForeignKeys { get; init; } = true;

    /// <summary>
    /// When true, create FKs as ENABLE NOVALIDATE to avoid failing on legacy data.
    /// </summary>
    public bool ForeignKeysEnableNoValidate { get; init; } = true;

}

