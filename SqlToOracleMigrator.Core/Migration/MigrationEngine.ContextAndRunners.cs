using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using SqlToOracleMigrator.Core.Migration;
using SqlToOracleMigrator.Core.Tracking;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    internal sealed record MigrationContext(
        MigrationEngine Engine,
        MigrationRequest Request,
        SqlConnection OpenSql,
        OracleConnection OpenOra,
        ToolMigRunInfo Run,
        string RunDir,
        Action<string> AppendLog,
        HashSet<string> CompletedStages,
        Func<string, string> GetTargetSchema,
        Func<MigrationStage, string, string?, int, Task> ToolMigStageAsync,
        Func<MigrationStage, string, string, string, string, string?, string?, Task> ToolMigObjectAsync,
        MigrationRunSummary Summary,
        int Dop,
        ErrorHandlingMode StageMode)
    {
        public List<(string Schema, string Table)> Tables { get; set; } = new();

        public List<(string Schema, string Name)> Sequences { get; set; } = new();
        public List<(string Schema, string Name)> Views { get; set; } = new();
        public List<(string Schema, string Name)> Procedures { get; set; } = new();
        public List<(string Schema, string Name)> Functions { get; set; } = new();
        public List<(string Schema, string Name, string ParentSchema, string ParentName)> Triggers { get; set; } = new();
        public List<(string Schema, string Name, string BaseObjectName)> Synonyms { get; set; } = new();
        public List<(string Schema, string Name, string UnderlyingType)> UserDefinedTypes { get; set; } = new();
        public List<SqlForeignKeyDef> ForeignKeys { get; set; } = new();
    }

    internal interface IMigrationStageRunner
    {
        MigrationStage Stage { get; }
        Task RunAsync(MigrationContext ctx, CancellationToken ct);
    }

    private List<IMigrationStageRunner> BuildStageRunners(MigrationContext ctx) => new()
    {
        new DiscoveryPlanningRunner(),
        new SchemaProvisioningRunner(),
        new DataDefValidationRunner(),
        new DdlGenerationRunner(),
        new DataValidationRunner(),
        new DataMigrationRunner(),
        new PostValidationRunner(),
        new FinalizationRunner()
    };

    internal sealed class MigrationRunSummary
    {
        public Guid RunId { get; set; }
        public int RunVersion { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public string SourceConnection { get; set; } = "";
        public string SourceDatabase { get; set; } = "";
        public string TargetConnection { get; set; } = "";
        public string TargetSchema { get; set; } = "";
        public int DegreeOfParallelism { get; set; }
        public int TableCount { get; set; }
        public string ErrorHandlingMode { get; set; } = "FailFast";
    }

    private sealed record StageError(string Stage, string Schema, string Object, string ErrorType, string Message, string? Details)
    {
        public static StageError FromException(MigrationStage stage, string schema, string obj, Exception ex)
            => new StageError(stage.ToString(), schema, obj, ex.GetType().Name, ex.Message, ex.ToString());
    }

    private sealed class StageFailedException : Exception
    {
        public MigrationStage Stage { get; }
        public List<StageError> Errors { get; }

        public StageFailedException(MigrationStage stage, List<StageError> errors)
            : base($"{stage} failed with {errors.Count} error(s).")
        {
            Stage = stage;
            Errors = errors;
        }
    }
}
