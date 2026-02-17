namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private static MigrationRequest CloneRequestWithBulkOverrides(
        MigrationRequest r,
        bool useOracleBulkCopy,
        int bulkCopyBatchSize,
        bool bulkCopyUseInternalTransaction)
    {
        // MigrationRequest is an init-only class; for per-table overrides we create a cloned request.
        return new MigrationRequest
        {
            SourceSqlConnection = r.SourceSqlConnection,
            SourceDatabase = r.SourceDatabase,
            TargetOracleConnection = r.TargetOracleConnection,
            TargetSchema = r.TargetSchema,
            DegreeOfParallelism = r.DegreeOfParallelism,

            CloneSourceSchemas = r.CloneSourceSchemas,
            AutoCreateTargetSchemas = r.AutoCreateTargetSchemas,
            EnableDataDefValidation = r.EnableDataDefValidation,
            EnableDataValidation = r.EnableDataValidation,
            DataValidationRowLimit = r.DataValidationRowLimit,
            ValidateFullDataset = r.ValidateFullDataset,

            ResumeRunId = r.ResumeRunId,
            ErrorHandlingMode = r.ErrorHandlingMode,

            OverrideTargetObjectsEachRun = r.OverrideTargetObjectsEachRun,
            EnsureTargetPdb = r.EnsureTargetPdb,
            DropTargetPdbIfExists = r.DropTargetPdbIfExists,
            TargetPdbName = r.TargetPdbName,

            CreateDependentObjects = r.CreateDependentObjects,
            CreateDependentObjectStubs = r.CreateDependentObjectStubs,
            CreateForeignKeys = r.CreateForeignKeys,
            ForeignKeysEnableNoValidate = r.ForeignKeysEnableNoValidate,

            PlanOption = r.PlanOption,
            RequireDirectPdbConnection = r.RequireDirectPdbConnection,
            AllowCdbConnectionFallback = r.AllowCdbConnectionFallback,

            UseOracleBulkCopy = useOracleBulkCopy,
            UseUnquotedUppercaseIdentifiers = r.UseUnquotedUppercaseIdentifiers,
            BulkCopyBatchSize = bulkCopyBatchSize,
            BulkCopyTimeoutSeconds = r.BulkCopyTimeoutSeconds,
            BulkCopyUseInternalTransaction = bulkCopyUseInternalTransaction,

            EnableSpatialXmlStaging = r.EnableSpatialXmlStaging,
            KeepStagingColumnsOnlyOnFailure = r.KeepStagingColumnsOnlyOnFailure,
            RunStage9ConversionBeforeConstraintsAndIndexes = r.RunStage9ConversionBeforeConstraintsAndIndexes,
            GatherSchemaStats = r.GatherSchemaStats,

            SecurityGrantMode = r.SecurityGrantMode,
            StrictFailOnMissingSecurityUsers = r.StrictFailOnMissingSecurityUsers,
            DoNotFailOnMissingAdExternalPrincipals = r.DoNotFailOnMissingAdExternalPrincipals,

            EnforceTablespaceMapping = r.EnforceTablespaceMapping,
            DefaultTablespace = r.DefaultTablespace,
            TempTablespace = r.TempTablespace
        };
    }
}
