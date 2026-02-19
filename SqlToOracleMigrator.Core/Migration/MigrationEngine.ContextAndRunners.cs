using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using SqlToOracleMigrator.Core.Migration;
using SqlToOracleMigrator.Core.Tracking;
using System.Collections.Concurrent;
using System.Linq;
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
        /// <summary>
        /// The stage currently executing (best-effort; used for run reporting).
        /// </summary>
        public MigrationStage CurrentStage { get; set; } = MigrationStage.ConnectionFingerprinting;

        /// <summary>
        /// Per-stage execution reports written to disk + summarized in RunSummary.html.
        /// </summary>
        public Dictionary<MigrationStage, StageExecutionReport> StageReports { get; } = new();

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

        private List<IMigrationStageRunner> BuildStageRunners(MigrationContext ctx)
    {
        // v1.1: 10-stage pipeline. For backwards compatibility, we delegate to existing runner implementations where possible.
        var all = new List<IMigrationStageRunner>
        {
            new ConnectionFingerprintingRunner(),
            new DeepDiscoveryRunner(),
            new PlanningTopologicalGraphRunner(),
            new ProvisioningRunner(),
            new DdlGenerationDryRunRunner(),
            new DeploymentSkeletonRunner(),
            new DataStrategySamplingRunner(),
            new ParallelDataMigrationRunner(),
            new PostLoadEnforcementRunner(),
            new FinalVerificationRunner()
        };

        // Plan option controls which stages are included; completed stages are skipped by the engine based on ToolMig.
        return ctx.Request.PlanOption switch
        {
            MigrationPlanOption.Feasibility => all.Where(r => r.Stage is MigrationStage.ConnectionFingerprinting or MigrationStage.DeepDiscovery or MigrationStage.PlanningTopologicalGraph).ToList(),
            MigrationPlanOption.DdlValidation => all.Where(r => r.Stage is MigrationStage.ConnectionFingerprinting or MigrationStage.DeepDiscovery or MigrationStage.PlanningTopologicalGraph or MigrationStage.Provisioning or MigrationStage.DdlGenerationDryRun or MigrationStage.DeploymentSkeleton).ToList(),
            MigrationPlanOption.DataValidation => all.Where(r => r.Stage is MigrationStage.ConnectionFingerprinting
                                                               or MigrationStage.DeepDiscovery
                                                               or MigrationStage.PlanningTopologicalGraph
                                                               or MigrationStage.Provisioning
                                                               or MigrationStage.DdlGenerationDryRun
                                                               or MigrationStage.DeploymentSkeleton
                                                               or MigrationStage.DataStrategySampling
                                                               or MigrationStage.ParallelDataMigration).ToList(),
            MigrationPlanOption.Migrate => all, // full pipeline but skip completed stages
            MigrationPlanOption.FullMigration => all,
            _ => all
        };
    }

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

        /// <summary>
        /// Phase confidence score (0..100). Computed during Phase 1 (Assess & Plan / Stage 3)
        /// and updated during later phases as needed.
        /// Used for ToolMig.GroupStatus updates and final certificate reporting.
        /// </summary>
        public int Confidence { get; set; } = 100;
    }

    internal sealed record StageError(string Stage, string Schema, string Object, string ErrorType, string Message, string? Details)
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
