namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private sealed class DdlGenerationRunner : IMigrationStageRunner
    {
        public MigrationStage Stage => MigrationStage.DdlGeneration;

        public async Task RunAsync(MigrationContext ctx, CancellationToken ct)
        {
            if (ctx.CompletedStages.Contains(MigrationStage.DdlGeneration.ToString()))
            {
                ctx.Engine.Raise(MigrationStage.DdlGeneration, "Skipping: already completed in prior run.");
                ctx.AppendLog("[DdlGeneration] Skipped (already completed).");
                return;
            }

            ctx.Engine.Raise(MigrationStage.DdlGeneration, "Generating and deploying DDL (tables + dependent objects)...");
            ctx.AppendLog("[DdlGeneration] Starting...");
            await ctx.ToolMigStageAsync(MigrationStage.DdlGeneration, "InProgress", "Generating + deploying DDL", 0);

            var errors = new List<StageError>();
            var completedObjects = ctx.Request.ResumeRunId.HasValue
                ? await ctx.Engine._toolMig.GetCompletedObjectsAsync(ctx.OpenSql, ctx.Run.RunId, MigrationStage.DdlGeneration.ToString(), ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            static string ObjKey(string schema, string name, string type) => $"{schema}.{name}|{type}";
            bool IsCompleted(string schema, string name, string type)
                => completedObjects.Contains(ObjKey(schema, name, type)) || completedObjects.Contains($"{schema}.{name}");

            async Task RunOneAsync(string schema, string name, string type, Func<Task> action)
            {
                ct.ThrowIfCancellationRequested();
                if (IsCompleted(schema, name, type)) return;

                await ctx.ToolMigObjectAsync(MigrationStage.DdlGeneration, schema, name, type, "InProgress", null, null);
                try
                {
                    await action();
                    await ctx.ToolMigObjectAsync(MigrationStage.DdlGeneration, schema, name, type, "Completed", null, null);
                }
                catch (Exception ex)
                {
                    errors.Add(StageError.FromException(MigrationStage.DdlGeneration, schema, name, ex));
                    await ctx.ToolMigObjectAsync(MigrationStage.DdlGeneration, schema, name, type, "Failed", ex.GetType().Name, ex.Message);
                    ctx.AppendLog($"[DdlGeneration][ERROR] {type} {schema}.{name}: {ex.Message}");
                    if (ctx.StageMode == ErrorHandlingMode.FailFast) throw;
                }
            }

            try
            {
                // IMPORTANT (Root Fix): Types + Sequences are prerequisites for table DDL.
                // Oracle resolves DEFAULT <schema>.<seq>.NEXTVAL at parse time, so sequences must exist
                // BEFORE any CREATE TABLE that references them. Likewise, some tables depend on UDTs.
                // Therefore, these are NOT governed by CreateDependentObjects (which controls views/procs/etc.).

                // 1) Types (UDT) - some tables may depend on UDTs
                foreach (var t in ctx.UserDefinedTypes)
                {
                    var schema = t.Schema;
                    var name = t.Name;
                    var baseType = t.UnderlyingType;
                    await RunOneAsync(schema, name, "TYPE", async () =>
                        await ctx.Engine.DeployUserDefinedTypeAsync(
                            ctx.OpenSql,
                            ctx.OpenOra,
                            ctx.Request.SourceDatabase,
                            schema,
                            name,
                            baseType,
                            ctx.GetTargetSchema(schema),
                            ctx.Request.CreateDependentObjectStubs,
                            ctx.RunDir,
                            ct));
                }

                // 2) Sequences - tables may use them in DEFAULT expressions
                foreach (var s in ctx.Sequences)
                {
                    var schema = s.Schema;
                    var name = s.Name;
                    await RunOneAsync(schema, name, "SEQUENCE", async () =>
                        await ctx.Engine.DeploySequenceAsync(
                            ctx.OpenSql,
                            ctx.OpenOra,
                            ctx.Request.SourceDatabase,
                            schema,
                            name,
                            ctx.GetTargetSchema(schema),
                            ct));
                }

                // After deploying sequences, grant SELECT on cross-schema sequences to all other schemas.
                // This is required for DEFAULT <schema>.<seq>.NEXTVAL and for cross-schema references in code.
                await ctx.Engine.GrantSequenceUsageAcrossSchemasAsync(ctx.OpenOra, ctx, ct);

                // Tables (with PK/UQ/Indexes) - run after sequences so DEFAULT ...NEXTVAL parses/executes cleanly.
                foreach (var t in ctx.Tables)
                {
                    var schema = t.Schema;
                    var name = t.Table;
                    await RunOneAsync(schema, name, "TABLE", async () =>
                        await ctx.Engine.DeployTableAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, ctx.GetTargetSchema(schema), ct));
                }

                if (ctx.Request.CreateDependentObjects)
                {
                    // Views
                    foreach (var v in ctx.Views)
                    {
                        var schema = v.Schema;
                        var name = v.Name;
                        await RunOneAsync(schema, name, "VIEW", async () =>
                            await ctx.Engine.DeployViewAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, ctx.GetTargetSchema(schema), ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct));
                    }

                    // Procedures
                    foreach (var p in ctx.Procedures)
                    {
                        var schema = p.Schema;
                        var name = p.Name;
                        await RunOneAsync(schema, name, "PROCEDURE", async () =>
                            await ctx.Engine.DeployProcedureAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, ctx.GetTargetSchema(schema), ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct));
                    }

                    // Functions
                    foreach (var f in ctx.Functions)
                    {
                        var schema = f.Schema;
                        var name = f.Name;
                        await RunOneAsync(schema, name, "FUNCTION", async () =>
                            await ctx.Engine.DeployFunctionAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, ctx.GetTargetSchema(schema), ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct));
                    }

                    // Triggers (need parent table/view)
                    foreach (var tr in ctx.Triggers)
                    {
                        var schema = tr.Schema;
                        var name = tr.Name;
                        var parentSchema = tr.ParentSchema;
                        var parentName = tr.ParentName;
                        await RunOneAsync(schema, name, "TRIGGER", async () =>
                            await ctx.Engine.DeployTriggerAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, parentSchema, parentName, ctx.GetTargetSchema(parentSchema), ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct));
                    }

                    // Synonyms
                    foreach (var syn in ctx.Synonyms)
                    {
                        var schema = syn.Schema;
                        var name = syn.Name;
                        var baseObj = syn.BaseObjectName;
                        await RunOneAsync(schema, name, "SYNONYM", async () =>
                            await ctx.Engine.DeploySynonymAsync(ctx.OpenOra, schema, name, baseObj, ctx.GetTargetSchema(schema), ct));
                    }
                }

                if (errors.Count > 0)
                    throw new StageFailedException(MigrationStage.DdlGeneration, errors);

                await ctx.ToolMigStageAsync(MigrationStage.DdlGeneration, "Completed", "DDL deployed", 0);
                ctx.AppendLog("[DdlGeneration] Completed.");
            }
            catch (StageFailedException sfe)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DdlGeneration, "Failed", sfe.Message, sfe.Errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, sfe.Stage.ToString(), sfe.Errors);
                throw;
            }
            catch (Exception ex)
            {
                await ctx.ToolMigStageAsync(MigrationStage.DdlGeneration, "Failed", ex.Message, errors.Count);
                ctx.Engine.WriteStageReport(ctx.RunDir, MigrationStage.DdlGeneration.ToString(), errors.Count > 0
                    ? errors
                    : new List<StageError> { StageError.FromException(MigrationStage.DdlGeneration, "", "", ex) });
                throw;
            }
        }
    }
}
