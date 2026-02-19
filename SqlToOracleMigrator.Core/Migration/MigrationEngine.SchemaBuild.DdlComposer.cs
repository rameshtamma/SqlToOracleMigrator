using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core.Oracle;
using System.Text;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Composes DDL statements for Phase 2 (Schema Build).
    /// Implemented as a nested type to access existing private DDL helper methods.
    /// </summary>
    private sealed class SchemaBuildDdlComposer
    {
        private readonly MigrationEngine _engine;

        public SchemaBuildDdlComposer(MigrationEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public async Task<SchemaBuildDdlBundle> ComposeAsync(MigrationContext ctx, CancellationToken ct)
        {
            var b = new SchemaBuildDdlBundle();

            // 1) UDTs
            foreach (var t in ctx.UserDefinedTypes)
            {
                ct.ThrowIfCancellationRequested();
                var targetSchema = ctx.GetTargetSchema(t.Schema);
                var schemaQ = OracleIdent.FormatSchema(targetSchema);
                var nameQ = OracleIdent.QuoteIdent(t.Name);
                var oracleScalar = string.IsNullOrWhiteSpace(t.UnderlyingType)
                    ? "VARCHAR2(4000)"
                    : _engine._typeMapper.Map(t.UnderlyingType, 4000, null, null);
                var ddl = $"CREATE OR REPLACE TYPE {schemaQ}.{nameQ} AS OBJECT (VALUE_ {oracleScalar})";
                b.Statements.Add(new SchemaBuildDdlStatement
                {
                    Schema = targetSchema,
                    ObjectName = t.Name,
                    ObjectType = "TYPE",
                    Sql = Header("TYPE", targetSchema, t.Name) + "\n" + ddl
                });
            }

            // 2) Sequences (best-effort defaults)
            foreach (var s in ctx.Sequences)
            {
                ct.ThrowIfCancellationRequested();
                var targetSchema = ctx.GetTargetSchema(s.Schema);
                var schemaQ = OracleIdent.FormatSchema(targetSchema);
                var nameQ = OracleIdent.QuoteIdent(s.Name);
                var ddl = $"CREATE SEQUENCE {schemaQ}.{nameQ} START WITH 1 INCREMENT BY 1";
                b.Statements.Add(new SchemaBuildDdlStatement
                {
                    Schema = targetSchema,
                    ObjectName = s.Name,
                    ObjectType = "SEQUENCE",
                    Sql = Header("SEQUENCE", targetSchema, s.Name) + "\n" + ddl
                });
            }

            // 3) Tables + constraints/indexes
            foreach (var t in ctx.Tables)
            {
                ct.ThrowIfCancellationRequested();
                var targetSchema = ctx.GetTargetSchema(t.Schema);
                var columns = await _engine._sqlMeta.GetTableColumnsAsync(ctx.OpenSql, ctx.Request.SourceDatabase, t.Schema, t.Table, ct);
                var create = OracleDdlGenerator.CreateTableDdl(targetSchema, t.Table, columns, _engine._typeMapper, ctx.Request.EnableSpatialXmlStaging);

                // Requirement: segment creation deferred (skeleton, faster deployment).
                if (!create.Contains("SEGMENT", StringComparison.OrdinalIgnoreCase))
                    create += " SEGMENT CREATION DEFERRED";

                b.Statements.Add(new SchemaBuildDdlStatement
                {
                    Schema = targetSchema,
                    ObjectName = t.Table,
                    ObjectType = "TABLE",
                    Sql = Header("TABLE", targetSchema, t.Table) + "\n" + create
                });

                // Constraints and indexes are produced per table (best-effort). These are safe for dry-run parsing.
                var keyDdls = await _engine.GenerateConstraintAndIndexDdlsAsync(ctx.OpenSql, ctx.OpenOra, ctx.Request.SourceDatabase, t.Schema, t.Table, targetSchema, ct);
                foreach (var d in keyDdls)
                {
                    b.Statements.Add(new SchemaBuildDdlStatement
                    {
                        Schema = targetSchema,
                        ObjectName = d.ObjectName,
                        ObjectType = d.ObjectType,
                        Sql = Header(d.ObjectType, targetSchema, d.ObjectName) + "\n" + d.Sql
                    });
                }
            }

            // 4) Dependent objects
            if (ctx.Request.CreateDependentObjects)
            {
                // Views / procs / functions / triggers / synonyms can be CREATE OR REPLACE and are idempotent.
                foreach (var v in ctx.Views)
                {
                    ct.ThrowIfCancellationRequested();
                    var targetSchema = ctx.GetTargetSchema(v.Schema);
                    var ddl = await _engine.GenerateViewDdlAsync(ctx.OpenSql, ctx.Request.SourceDatabase, v.Schema, v.Name, targetSchema, ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct);
                    b.Statements.Add(new SchemaBuildDdlStatement { Schema = targetSchema, ObjectName = v.Name, ObjectType = "VIEW", Sql = Header("VIEW", targetSchema, v.Name) + "\n" + ddl });
                }
                foreach (var p in ctx.Procedures)
                {
                    ct.ThrowIfCancellationRequested();
                    var targetSchema = ctx.GetTargetSchema(p.Schema);
                    var ddl = await _engine.GenerateProcedureDdlAsync(ctx.OpenSql, ctx.Request.SourceDatabase, p.Schema, p.Name, targetSchema, ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct);
                    b.Statements.Add(new SchemaBuildDdlStatement { Schema = targetSchema, ObjectName = p.Name, ObjectType = "PROCEDURE", Sql = Header("PROCEDURE", targetSchema, p.Name) + "\n" + ddl });
                }
                foreach (var f in ctx.Functions)
                {
                    ct.ThrowIfCancellationRequested();
                    var targetSchema = ctx.GetTargetSchema(f.Schema);
                    var ddl = await _engine.GenerateFunctionDdlAsync(ctx.OpenSql, ctx.Request.SourceDatabase, f.Schema, f.Name, targetSchema, ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct);
                    b.Statements.Add(new SchemaBuildDdlStatement { Schema = targetSchema, ObjectName = f.Name, ObjectType = "FUNCTION", Sql = Header("FUNCTION", targetSchema, f.Name) + "\n" + ddl });
                }
                foreach (var tr in ctx.Triggers)
                {
                    ct.ThrowIfCancellationRequested();
                    var parentTargetSchema = ctx.GetTargetSchema(tr.ParentSchema);
                    var ddl = await _engine.GenerateTriggerDdlAsync(ctx.OpenSql, ctx.Request.SourceDatabase, tr.Schema, tr.Name, tr.ParentName, parentTargetSchema, ctx.Request.CreateDependentObjectStubs, ctx.RunDir, ct);
                    b.Statements.Add(new SchemaBuildDdlStatement { Schema = parentTargetSchema, ObjectName = tr.Name, ObjectType = "TRIGGER", Sql = Header("TRIGGER", parentTargetSchema, tr.Name) + "\n" + ddl });
                }
                foreach (var syn in ctx.Synonyms)
                {
                    ct.ThrowIfCancellationRequested();
                    var targetSchema = ctx.GetTargetSchema(syn.Schema);
                    var schemaQ = OracleIdent.FormatSchema(targetSchema);
                    var synQ = OracleIdent.QuoteIdent(syn.Name);
                    var ddl = $"CREATE OR REPLACE SYNONYM {schemaQ}.{synQ} FOR {syn.BaseObjectName}";
                    b.Statements.Add(new SchemaBuildDdlStatement { Schema = targetSchema, ObjectName = syn.Name, ObjectType = "SYNONYM", Sql = Header("SYNONYM", targetSchema, syn.Name) + "\n" + ddl });
                }
            }

            return b;
        }

        private static string Header(string type, string schema, string name)
            => $"-- {type} {schema}.{name}";
    }

    private sealed record GeneratedDdl(string ObjectType, string ObjectName, string Sql);

    /// <summary>
    /// Produces constraint/index DDL without executing it.
    /// </summary>
    private async Task<List<GeneratedDdl>> GenerateConstraintAndIndexDdlsAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string sourceSchema,
        string table,
        string targetSchema,
        CancellationToken ct)
    {
        // Keep identifier formatting consistent across TABLE DDL and CONSTRAINT/INDEX DDL.
        // If we create tables unquoted (UPPERCASE), constraints/indexes must reference the same names.
        var preferUnquotedUpper = _requestAccessor?.Invoke()?.UseUnquotedUppercaseIdentifiers ?? true;

        var keys = await GetSqlKeyConstraintsAsync(openSql, dbName, sourceSchema, table, ct);
        var indexes = await GetSqlIndexesAsync(openSql, dbName, sourceSchema, table, ct);
        var keyConstraintNames = new HashSet<string>(keys.Select(k => k.Name), StringComparer.OrdinalIgnoreCase);
        var oraCols = await GetOracleColumnInfoAsync(openOra, targetSchema, table, ct);

        var list = new List<GeneratedDdl>();

        foreach (var k in keys)
        {
            if (ContainsLobColumn(k.Columns, oraCols, out _))
                continue;
            if (ShouldSkipDueToKeyLength(sourceSchema, table, $"constraint '{k.Name}'", k.Columns, oraCols))
                continue;
            var ddl = BuildOracleConstraintDdl(targetSchema, table, k, preferUnquotedUpper);
            list.Add(new GeneratedDdl("CONSTRAINT", k.Name, ddl));
        }

        foreach (var ix in indexes)
        {
            if (keyConstraintNames.Contains(ix.Name))
                continue;
            if (ContainsLobColumn(ix.KeyColumns, oraCols, out _))
                continue;
            if (ShouldSkipDueToKeyLength(sourceSchema, table, $"index '{ix.Name}'", ix.KeyColumns, oraCols))
                continue;
            var ddl = BuildOracleIndexDdl(targetSchema, table, ix, preferUnquotedUpper);
            list.Add(new GeneratedDdl("INDEX", ix.Name, ddl));
        }

        return list;
    }
}