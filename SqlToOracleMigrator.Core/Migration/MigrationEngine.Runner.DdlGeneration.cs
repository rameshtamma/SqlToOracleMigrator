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
                // 1) Tables (with PK/UQ/Indexes)
                foreach (var t in ctx.Tables)
                {
                    var schema = t.Schema;
                    var name = t.Table;
                    await RunOneAsync(schema, name, "TABLE", async () =>
                        await ctx.Engine.DeployTableAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, ctx.GetTargetSchema(schema), ct));
                }

                if (ctx.Request.CreateDependentObjects)
                {
                    // 2) Types (UDT)
                    foreach (var t in ctx.UserDefinedTypes)
                    {
                        var schema = t.Schema;
                        var name = t.Name;
                        var baseType = t.UnderlyingType;
                        await RunOneAsync(schema, name, "TYPE", async () =>
                            await ctx.Engine.DeployUserDefinedTypeAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, baseType, ctx.GetTargetSchema(schema), ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct));
                    }

                    // 3) Sequences
                    foreach (var s in ctx.Sequences)
                    {
                        var schema = s.Schema;
                        var name = s.Name;
                        await RunOneAsync(schema, name, "SEQUENCE", async () =>
                            await ctx.Engine.DeploySequenceAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, ctx.GetTargetSchema(schema), ct));
                    }

                    // 4) Views
                    foreach (var v in ctx.Views)
                    {
                        var schema = v.Schema;
                        var name = v.Name;
                        await RunOneAsync(schema, name, "VIEW", async () =>
                            await ctx.Engine.DeployViewAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, ctx.GetTargetSchema(schema), ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct));
                    }

                    // 5) Procedures
                    foreach (var p in ctx.Procedures)
                    {
                        var schema = p.Schema;
                        var name = p.Name;
                        await RunOneAsync(schema, name, "PROCEDURE", async () =>
                            await ctx.Engine.DeployProcedureAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, ctx.GetTargetSchema(schema), ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct));
                    }

                    // 6) Functions
                    foreach (var f in ctx.Functions)
                    {
                        var schema = f.Schema;
                        var name = f.Name;
                        await RunOneAsync(schema, name, "FUNCTION", async () =>
                            await ctx.Engine.DeployFunctionAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, ctx.GetTargetSchema(schema), ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct));
                    }

                    // 7) Triggers (need parent table/view)
                    foreach (var tr in ctx.Triggers)
                    {
                        var schema = tr.Schema;
                        var name = tr.Name;
                        var parentSchema = tr.ParentSchema;
                        var parentName = tr.ParentName;
                        await RunOneAsync(schema, name, "TRIGGER", async () =>
                            await ctx.Engine.DeployTriggerAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, schema, name, parentSchema, parentName, ctx.GetTargetSchema(parentSchema), ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct));
                    }

                    // 8) Synonyms
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
