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
    private sealed class DiscoveryPlanningRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.Discovery;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            ctx.Engine.Raise(MigrationStage.Discovery, "Discovering tables...");
            ctx.AppendLog("[Discovery] Discovering tables...");

            var tables = await ctx.Engine.DiscoverTablesAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);
            ctx.Tables = tables;
            ctx.Summary.TableCount = tables.Count;

            ctx.Engine.Raise(MigrationStage.Planning, $"Planning migration for {tables.Count} tables.");
            ctx.AppendLog($"[Planning] Tables discovered: {tables.Count}");
        }
    }
}
