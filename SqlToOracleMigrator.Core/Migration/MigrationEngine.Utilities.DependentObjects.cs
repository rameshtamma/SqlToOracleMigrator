using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using System.Text;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }


    private static async Task<string?> GetSqlModuleDefinitionAsync(SqlConnection openSql, string dbName, string schema, string objectName, CancellationToken ct)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT m.definition
FROM {db}.sys.sql_modules m
JOIN {db}.sys.objects o ON m.object_id = o.object_id
JOIN {db}.sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = @SchemaName AND o.name = @ObjectName;";

        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@SchemaName", schema);
        cmd.Parameters.AddWithValue("@ObjectName", objectName);

        var obj = await cmd.ExecuteScalarAsync(ct);
        return obj is null or DBNull ? null : Convert.ToString(obj);
    }

    private static void WriteDefinitionArtifact(string runDir, string kind, string schema, string name, string? sqlText)
    {
        if (string.IsNullOrWhiteSpace(runDir) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            var safeKind = SanitizeFileName(kind);
            var folder = Path.Combine(runDir, "SourceDefinitions", safeKind);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"{SanitizeFileName(schema)}.{SanitizeFileName(name)}.sql");
            File.WriteAllText(file, sqlText ?? string.Empty);
        }
        catch
        {
            // ignore artifact failures
        }
    }

    private static async Task ExecuteOracleWithFallbackAsync(OracleConnection openOra, string primaryDdl, string? fallbackDdl, bool allowFallback, CancellationToken ct)
    {
        try
        {
            await using var cmd = new OracleCommand(primaryDdl, openOra);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            if (!allowFallback || string.IsNullOrWhiteSpace(fallbackDdl)) throw;
            await using var cmd2 = new OracleCommand(fallbackDdl, openOra);
            await cmd2.ExecuteNonQueryAsync(ct);
        }
    }

    private static string StripSqlServerBatchNoise(string sql)
    {
        var lines = sql.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Equals("GO", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string BasicSqlToOracleTextTransforms(string sql)
    {
        // Very conservative transformations: remove brackets, common functions.
        var s = sql.Replace("[", "").Replace("]", "");
        s = s.Replace("GETDATE()", "SYSDATE", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("NEWID()", "SYS_GUID()", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("ISNULL(", "NVL(", StringComparison.OrdinalIgnoreCase);
        return s;
    }

    public async Task DeploySequenceAsync(SqlConnection openSql, OracleConnection openOra, string dbName, string schema, string sequenceName, string targetSchema, CancellationToken ct)
    {
        // Pull basic sequence properties from SQL Server
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT CAST(start_value AS BIGINT) AS start_value, CAST(increment AS BIGINT) AS increment_value,
       CAST(minimum_value AS BIGINT) AS min_value, CAST(maximum_value AS BIGINT) AS max_value,
       is_cycling, cache_size
FROM {db}.sys.sequences seq
JOIN {db}.sys.schemas s ON seq.schema_id = s.schema_id
WHERE s.name = @SchemaName AND seq.name = @SeqName;";

        long start = 1, inc = 1, min = 1, max = 0, cache = 0;
        bool cycling = false;

        await using (var cmd = new SqlCommand(sql, openSql))
        {
            cmd.Parameters.AddWithValue("@SchemaName", schema);
            cmd.Parameters.AddWithValue("@SeqName", sequenceName);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                start = rdr.IsDBNull(0) ? 1 : rdr.GetInt64(0);
                inc = rdr.IsDBNull(1) ? 1 : rdr.GetInt64(1);
                min = rdr.IsDBNull(2) ? 1 : rdr.GetInt64(2);
                max = rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3);
                cycling = !rdr.IsDBNull(4) && rdr.GetBoolean(4);
                cache = rdr.IsDBNull(5) ? 0 : rdr.GetInt64(5);
            }
        }

        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var nameQ = OracleIdent.QuoteIdent(sequenceName);

        var drop = $"BEGIN EXECUTE IMMEDIATE 'DROP SEQUENCE {schemaQ}.{nameQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using (var dcmd = new OracleCommand(drop, openOra))
            await dcmd.ExecuteNonQueryAsync(ct);

        var sb = new StringBuilder();
        sb.Append($"CREATE SEQUENCE {schemaQ}.{nameQ} START WITH {Math.Max(1, start)} INCREMENT BY {inc}");
        if (min > 0) sb.Append($" MINVALUE {min}");
        if (max > 0) sb.Append($" MAXVALUE {max}");
        sb.Append(cycling ? " CYCLE" : " NOCYCLE");
        if (cache > 0) sb.Append($" CACHE {cache}");
        else sb.Append(" NOCACHE");

        await using var ocmd = new OracleCommand(sb.ToString(), openOra);
        await ocmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeployUserDefinedTypeAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string schema,
        string typeName,
        string baseType,
        string targetSchema,
        bool allowStub,
        string runDir,
        CancellationToken ct)
    {
        // SQL Server alias types do not map cleanly; create a minimal Oracle object type stub.
        // Store the SQL Server metadata for manual porting.
        WriteDefinitionArtifact(runDir, "TYPE", schema, typeName, $"-- SQL Server UDT: {schema}.{typeName} underlying={baseType}");

        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var nameQ = OracleIdent.QuoteIdent(typeName);

        var drop = $"BEGIN EXECUTE IMMEDIATE 'DROP TYPE {schemaQ}.{nameQ} FORCE'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using (var dcmd = new OracleCommand(drop, openOra))
            await dcmd.ExecuteNonQueryAsync(ct);

        // Attempt a simple scalar type mapping for the single attribute.
        var oracleScalar = string.IsNullOrWhiteSpace(baseType)
            ? "VARCHAR2(4000)"
            : _typeMapper.Map(baseType, 4000, null, null);

        var primary = $"CREATE OR REPLACE TYPE {schemaQ}.{nameQ} AS OBJECT (VALUE_ {oracleScalar})";
        var fallback = $"CREATE OR REPLACE TYPE {schemaQ}.{nameQ} AS OBJECT (VALUE_ VARCHAR2(4000))";

        await ExecuteOracleWithFallbackAsync(openOra, primary, fallback, allowStub, ct);
    }

    public async Task DeployViewAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string schema,
        string viewName,
        string targetSchema,
        bool allowStub,
        string runDir,
        CancellationToken ct)
    {
        var def = await GetSqlModuleDefinitionAsync(openSql, dbName, schema, viewName, ct) ?? string.Empty;
        WriteDefinitionArtifact(runDir, "VIEW", schema, viewName, def);

        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var nameQ = OracleIdent.QuoteIdent(viewName);

        var drop = $"BEGIN EXECUTE IMMEDIATE 'DROP VIEW {schemaQ}.{nameQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using (var dcmd = new OracleCommand(drop, openOra))
            await dcmd.ExecuteNonQueryAsync(ct);

        // Attempt naive transform; if it fails, create a stub view.
        var text = BasicSqlToOracleTextTransforms(StripSqlServerBatchNoise(def));
        // Replace CREATE VIEW with CREATE OR REPLACE VIEW and schema qualification.
        text = ReplaceCreateHeader(text, "VIEW", schema, viewName, schemaQ, nameQ);

        var stub = $"CREATE OR REPLACE VIEW {schemaQ}.{nameQ} AS SELECT CAST(NULL AS NUMBER) AS DUMMY FROM DUAL WHERE 1=0";
        await ExecuteOracleWithFallbackAsync(openOra, text, stub, allowStub, ct);
    }

    public async Task DeployProcedureAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string schema,
        string procName,
        string targetSchema,
        bool allowStub,
        string runDir,
        CancellationToken ct)
    {
        var def = await GetSqlModuleDefinitionAsync(openSql, dbName, schema, procName, ct) ?? string.Empty;
        WriteDefinitionArtifact(runDir, "PROCEDURE", schema, procName, def);

        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var nameQ = OracleIdent.QuoteIdent(procName);

        var drop = $"BEGIN EXECUTE IMMEDIATE 'DROP PROCEDURE {schemaQ}.{nameQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using (var dcmd = new OracleCommand(drop, openOra))
            await dcmd.ExecuteNonQueryAsync(ct);

        var text = BasicSqlToOracleTextTransforms(StripSqlServerBatchNoise(def));
        text = ReplaceCreateHeader(text, "PROCEDURE", schema, procName, schemaQ, nameQ);

        var stub = $"CREATE OR REPLACE PROCEDURE {schemaQ}.{nameQ} IS BEGIN NULL; END;";
        await ExecuteOracleWithFallbackAsync(openOra, text, stub, allowStub, ct);
    }

    public async Task DeployFunctionAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string schema,
        string funcName,
        string targetSchema,
        bool allowStub,
        string runDir,
        CancellationToken ct)
    {
        var def = await GetSqlModuleDefinitionAsync(openSql, dbName, schema, funcName, ct) ?? string.Empty;
        WriteDefinitionArtifact(runDir, "FUNCTION", schema, funcName, def);

        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var nameQ = OracleIdent.QuoteIdent(funcName);

        var drop = $"BEGIN EXECUTE IMMEDIATE 'DROP FUNCTION {schemaQ}.{nameQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using (var dcmd = new OracleCommand(drop, openOra))
            await dcmd.ExecuteNonQueryAsync(ct);

        var text = BasicSqlToOracleTextTransforms(StripSqlServerBatchNoise(def));
        text = ReplaceCreateHeader(text, "FUNCTION", schema, funcName, schemaQ, nameQ);

        // Fallback stub: return NUMBER
        var stub = $"CREATE OR REPLACE FUNCTION {schemaQ}.{nameQ} RETURN NUMBER IS BEGIN RETURN NULL; END;";
        await ExecuteOracleWithFallbackAsync(openOra, text, stub, allowStub, ct);
    }

    public async Task DeployTriggerAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string triggerSchema,
        string triggerName,
        string parentSchema,
        string parentName,
        string parentTargetSchema,
        bool allowStub,
        string runDir,
        CancellationToken ct)
    {
        var def = await GetSqlModuleDefinitionAsync(openSql, dbName, triggerSchema, triggerName, ct) ?? string.Empty;
        WriteDefinitionArtifact(runDir, "TRIGGER", triggerSchema, triggerName, def);

        var schemaQ = OracleIdent.FormatSchema(parentTargetSchema);
        var trgSchemaQ = OracleIdent.FormatSchema(parentTargetSchema);
        var trgNameQ = OracleIdent.QuoteIdent(triggerName);
        var parentQ = OracleIdent.QuoteIdent(parentName);

        var drop = $"BEGIN EXECUTE IMMEDIATE 'DROP TRIGGER {trgSchemaQ}.{trgNameQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using (var dcmd = new OracleCommand(drop, openOra))
            await dcmd.ExecuteNonQueryAsync(ct);

        // Naive replacement rarely works for triggers; create stub on target parent.
        var stub = $"CREATE OR REPLACE TRIGGER {trgSchemaQ}.{trgNameQ} BEFORE INSERT OR UPDATE OR DELETE ON {schemaQ}.{parentQ} BEGIN NULL; END;";

        // Try transformed text first (best effort)
        var text = BasicSqlToOracleTextTransforms(StripSqlServerBatchNoise(def));
        text = ReplaceCreateHeader(text, "TRIGGER", triggerSchema, triggerName, trgSchemaQ, trgNameQ);

        await ExecuteOracleWithFallbackAsync(openOra, text, stub, allowStub, ct);
    }

    public async Task DeploySynonymAsync(
        OracleConnection openOra,
        string schema,
        string synonymName,
        string baseObjectName,
        string targetSchema,
        CancellationToken ct)
    {
        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var synQ = OracleIdent.QuoteIdent(synonymName);

        var drop = $"BEGIN EXECUTE IMMEDIATE 'DROP SYNONYM {schemaQ}.{synQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using (var dcmd = new OracleCommand(drop, openOra))
            await dcmd.ExecuteNonQueryAsync(ct);

        // base_object_name might be [db].[schema].[object] or [schema].[object] or schema.object
        var cleaned = (baseObjectName ?? string.Empty).Replace("[", "").Replace("]", "").Trim();
        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string refSchema;
        string refObj;
        if (parts.Length >= 2)
        {
            refSchema = parts[^2];
            refObj = parts[^1];
        }
        else
        {
            refSchema = targetSchema;
            refObj = cleaned;
        }

        var refSchemaQ = OracleIdent.FormatSchema(refSchema);
        var refObjQ = OracleIdent.QuoteIdent(refObj);

        var ddl = $"CREATE OR REPLACE SYNONYM {schemaQ}.{synQ} FOR {refSchemaQ}.{refObjQ}";
        await using var cmd = new OracleCommand(ddl, openOra);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string ReplaceCreateHeader(string sql, string kind, string sourceSchema, string sourceName, string targetSchemaQ, string targetNameQ)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        var s = sql;
        // Try to replace the first occurrence of CREATE [OR ALTER] <kind> <schema>.<name>
        var normalized = s.Replace("CREATE OR ALTER", "CREATE", StringComparison.OrdinalIgnoreCase);
        var find1 = $"CREATE {kind} {sourceSchema}.{sourceName}";
        var find2 = $"CREATE {kind} {sourceName}";
        if (normalized.Contains(find1, StringComparison.OrdinalIgnoreCase))
            normalized = ReplaceFirstCI(normalized, find1, $"CREATE OR REPLACE {kind} {targetSchemaQ}.{targetNameQ}");
        else if (normalized.Contains(find2, StringComparison.OrdinalIgnoreCase))
            normalized = ReplaceFirstCI(normalized, find2, $"CREATE OR REPLACE {kind} {targetSchemaQ}.{targetNameQ}");
        else
            normalized = $"CREATE OR REPLACE {kind} {targetSchemaQ}.{targetNameQ}\n" + normalized;

        return normalized;
    }

    private static string ReplaceFirstCI(string input, string find, string replace)
    {
        var idx = input.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return input;
        return input.Substring(0, idx) + replace + input.Substring(idx + find.Length);
    }
}
