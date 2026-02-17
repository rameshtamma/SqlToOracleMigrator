using System.Threading;
using System.Threading.Tasks;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    // v1.1 pipeline wrapper runners. These delegate to existing runners to preserve prior functionality,
    // while presenting the new 10-stage timeline and allowing incremental refactors.

    private sealed class ConnectionFingerprintingRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.ConnectionFingerprinting;

        public Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            // PDB enforcement handled during provisioning / connection setup today; left as a light-weight stage.
            ctx.AppendLog("Stage 1: Connection fingerprinting (v1.1) - OK");
            return Task.CompletedTask;
        }
    }

    private sealed class DeepDiscoveryRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DeepDiscovery;

        public Task RunAsync(MigrationContext ctx, CancellationToken ct)
            => new DiscoveryPlanningRunner().RunAsync(ctx, ct);
    }

    private sealed class PlanningTopologicalGraphRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.PlanningTopologicalGraph;

        public Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            // Planning is currently done as part of DiscoveryPlanningRunner. This stage is a placeholder for future split.
            ctx.AppendLog("Stage 3: Planning/topological graph (v1.1) - produced in Stage 2 currently");
            return Task.CompletedTask;
        }
    }

    private sealed class ProvisioningRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.Provisioning;
        public Task RunAsync(MigrationContext ctx, CancellationToken ct) => new SchemaBuildProvisioningRunner().RunAsync(ctx, ct);
    }

    private sealed class DdlGenerationDryRunRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DdlGenerationDryRun;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            // Stage 5 (v1.2): DDL Validation
            // - Generate DDL scripts for the target
            // - Validate syntax via DBMS_SQL.PARSE (parse-only)
            // - Store validated scripts + validation report in ToolMig.RunArtifacts
            await new SchemaBuildDdlValidationRunner().RunAsync(ctx, ct);
        }
    }

    private sealed class DeploymentSkeletonRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DeploymentSkeleton;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            // Stage 6 (v1.2): DDL Deployment
            // - Deploy tables + dependent objects using idempotent PL/SQL wrappers
            // - Resume: skip Success; retry Pending/Error
            await new SchemaBuildDdlDeploymentRunner().RunAsync(ctx, ct);
        }
    }

    private sealed class DataStrategySamplingRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DataStrategySampling;
        public Task RunAsync(MigrationContext ctx, CancellationToken ct) => new DataPrepStrategySamplingV12Runner().RunAsync(ctx, ct);
    }

    private sealed class ParallelDataMigrationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.ParallelDataMigration;
        public Task RunAsync(MigrationContext ctx, CancellationToken ct) => new DataPrepParallelDataMigrationV12Runner().RunAsync(ctx, ct);
    }

    private sealed class PostLoadEnforcementRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.PostLoadEnforcement;
        public Task RunAsync(MigrationContext ctx, CancellationToken ct) => new PostLoadEnforcementV12Runner().RunAsync(ctx, ct);
    }

    private sealed class FinalVerificationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.FinalVerification;
        public Task RunAsync(MigrationContext ctx, CancellationToken ct) => new FinalVerificationV12Runner().RunAsync(ctx, ct);
    }
}
