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
    // ---------
    // Legacy stages (v5/v6). Kept for backward compatibility (ToolMig resume).
    // ---------
    Discovery = 0,
    Planning = 1,
    SchemaProvisioning = 2,
    DataDefValidation = 3,
    DdlGeneration = 4,
    PreValidation = 5,
    DataValidation = 6,
    DataMigration = 7,
    PostValidation = 8,
    Finalization = 9,

    // ---------
    // v1.1 upgraded 10-stage pipeline (preferred)
    // ---------
    ConnectionFingerprinting = 100,
    DeepDiscovery = 101,
    PlanningTopologicalGraph = 102,
    Provisioning = 103,
    DdlGenerationDryRun = 104,
    DeploymentSkeleton = 105,
    DataStrategySampling = 106,
    ParallelDataMigration = 107,
    PostLoadEnforcement = 108,
    FinalVerification = 109
}


public enum MigrationPhaseGroup
{
    ConnectAndAssess = 0,
    PlanAndPrepare = 1,
    BuildAndLoad = 2,
    EnforceAndVerify = 3
}

public enum MigrationPlanOption
{
    Feasibility = 0,
    DdlValidation = 1,
    DataValidation = 2,
    Migrate = 3,
    FullMigration = 4
}

public enum SecurityGrantMode
{
    ScriptOnly = 0,
    AutoApplyRolesAndObjectGrants = 1
}



public enum ErrorHandlingMode
{
    FailFast = 0,
    CollectAll = 1
}
