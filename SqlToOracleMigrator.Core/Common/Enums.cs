namespace SqlToOracleMigrator.Core;

public enum DatabaseEngine
{
    SqlServer = 0,
    Oracle = 1
}

public enum ConnectionTestStatus
{
    Yellow = 0, // saved but not tested
    Green = 1,  // last test succeeded
    Red = 2     // last test failed
}

public enum AppLogLevel
{
    Info,
    Warn,
    Error
}

public enum MigrationStage
{
    Discovery,
    Planning,
    // v6 additions
    SchemaProvisioning,
    DataDefValidation,
    DdlGeneration,
    // Legacy placeholder (v5). Kept for backward compatibility.
    PreValidation,
    // v6 additions
    DataValidation,
    DataMigration,
    PostValidation,
    Finalization
}


public enum ErrorHandlingMode
{
    FailFast = 0,
    CollectAll = 1
}
