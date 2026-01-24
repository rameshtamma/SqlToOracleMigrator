namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private sealed class DiscoveryPlanningRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.Discovery;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            ctx.Engine.Raise(MigrationStage.Discovery, "Discovering tables and dependent objects...");
            ctx.AppendLog("[Discovery] Discovering tables and dependent objects...");

            ctx.Tables = await ctx.Engine.DiscoverTablesAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);
            ctx.Sequences = await ctx.Engine.DiscoverSequencesAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);
            ctx.Views = await ctx.Engine.DiscoverViewsAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);
            ctx.Procedures = await ctx.Engine.DiscoverProceduresAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);
            ctx.Functions = await ctx.Engine.DiscoverFunctionsAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);
            ctx.Triggers = await ctx.Engine.DiscoverTriggersAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);
            ctx.Synonyms = await ctx.Engine.DiscoverSynonymsAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);
            ctx.UserDefinedTypes = await ctx.Engine.DiscoverUserDefinedTypesAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);
            ctx.ForeignKeys = await ctx.Engine.DiscoverForeignKeysAsync(ctx.OpenSql, ctx.Request.SourceDatabase, ct);

            ctx.Summary.TableCount = ctx.Tables.Count;

            ctx.Engine.Raise(MigrationStage.Planning, $"Planning migration: {ctx.Tables.Count} tables, {ctx.Views.Count} views, {ctx.Procedures.Count} procs, {ctx.Functions.Count} funcs, {ctx.Triggers.Count} triggers, {ctx.Synonyms.Count} synonyms, {ctx.Sequences.Count} sequences, {ctx.UserDefinedTypes.Count} types, {ctx.ForeignKeys.Count} FKs.");
            ctx.AppendLog($"[Planning] Tables={ctx.Tables.Count}, Views={ctx.Views.Count}, Procs={ctx.Procedures.Count}, Funcs={ctx.Functions.Count}, Triggers={ctx.Triggers.Count}, Synonyms={ctx.Synonyms.Count}, Sequences={ctx.Sequences.Count}, Types={ctx.UserDefinedTypes.Count}, FKs={ctx.ForeignKeys.Count}");
        }
    }
}
