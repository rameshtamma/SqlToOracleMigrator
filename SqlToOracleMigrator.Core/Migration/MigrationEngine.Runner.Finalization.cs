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
    private sealed class FinalizationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.Finalization;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            ctx.Summary.CompletedUtc = DateTimeOffset.UtcNow;
            ctx.Engine.Raise(MigrationStage.Finalization, "Writing run summary...");
            ctx.AppendLog("[Finalization] Writing run summary...");
            await ctx.ToolMigStageAsync(MigrationStage.Finalization, "InProgress", "Finalizing", 0);

            ctx.Engine.WriteRunSummary(ctx.RunDir, ctx.Summary);
            await ctx.ToolMigStageAsync(MigrationStage.Finalization, "Completed", "Completed", 0);
        }
    }
}
