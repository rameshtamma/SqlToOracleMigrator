using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using System.Text;
using System.Text.RegularExpressions;

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

    
    private sealed record SqlParamMeta(int ParameterId, string Name, string TypeName, int MaxLength, int Precision, int Scale, bool IsOutput);

    private static async Task<List<SqlParamMeta>> GetSqlParameterMetadataAsync(
        SqlConnection openSql,
        string dbName,
        string schema,
        string objectName,
        CancellationToken ct)
    {
        var db = SqlIdent.Bracket(dbName);
        var sql = $@"SELECT p.parameter_id, p.name, t.name AS type_name, p.max_length, p.precision, p.scale, p.is_output
FROM {db}.sys.parameters p
JOIN {db}.sys.objects o ON p.object_id = o.object_id
JOIN {db}.sys.schemas s ON o.schema_id = s.schema_id
JOIN {db}.sys.types t ON p.user_type_id = t.user_type_id
WHERE s.name = @SchemaName AND o.name = @ObjectName
ORDER BY p.parameter_id;";

        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@SchemaName", schema);
        cmd.Parameters.AddWithValue("@ObjectName", objectName);

        var result = new List<SqlParamMeta>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var pid = rdr.GetInt32(0);
            var name = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1);
            var typeName = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2);
            var maxLen = rdr.IsDBNull(3) ? 0 : Convert.ToInt32(rdr.GetValue(3));
            var prec = rdr.IsDBNull(4) ? 0 : Convert.ToInt32(rdr.GetValue(4));
            var scale = rdr.IsDBNull(5) ? 0 : Convert.ToInt32(rdr.GetValue(5));
            var isOut = !rdr.IsDBNull(6) && rdr.GetBoolean(6);

            result.Add(new SqlParamMeta(pid, name, typeName, maxLen, prec, scale, isOut));
        }
        return result;
    }

    private static string FormatSqlTypeToken(string typeName, int maxLength, int precision, int scale)
    {
        var t = (typeName ?? string.Empty).Trim();
        if (t.Length == 0) return string.Empty;

        var tl = t.ToLowerInvariant();

        // Character/binary lengths are in bytes; NVARCHAR/NCHAR are UTF-16 (2 bytes/char).
        if (tl is "varchar" or "char" or "varbinary" or "binary")
        {
            if (maxLength == -1) return $"{t}(max)";
            return $"{t}({Math.Max(1, maxLength)})";
        }

        if (tl is "nvarchar" or "nchar")
        {
            if (maxLength == -1) return $"{t}(max)";
            var chars = Math.Max(1, maxLength / 2);
            return $"{t}({chars})";
        }

        if (tl is "decimal" or "numeric")
        {
            if (precision > 0) return $"{t}({precision},{Math.Max(0, scale)})";
            return t;
        }

        // Most other types don't need size in the token for our Oracle mapping.
        return t;
    }

    private static async Task<string> BuildFunctionStubFromSqlMetadataAsync(
        SqlConnection openSql,
        string dbName,
        string sourceSchema,
        string funcName,
        string targetSchemaQ,
        string? tsqlDefinition,
        CancellationToken ct)
    {
        // Prefer sys.parameters (authoritative) over regex parsing of the T-SQL header.
        var metas = await GetSqlParameterMetadataAsync(openSql, dbName, sourceSchema, funcName, ct);

        // Return type for SQL scalar functions is typically parameter_id = 0 in sys.parameters.
        var retMeta = metas.FirstOrDefault(m => m.ParameterId == 0);
        var returnType = retMeta is not null && retMeta.ParameterId == 0
            ? FormatSqlTypeToken(retMeta.TypeName, retMeta.MaxLength, retMeta.Precision, retMeta.Scale)
            : ExtractSqlServerReturnType(tsqlDefinition);

        var oracleReturn = MapSqlTypeToOracle(returnType);

        var oracleParams = new List<string>();
        foreach (var m in metas.Where(m => m.ParameterId > 0))
        {
            var n = (m.Name ?? string.Empty).Trim();
            n = n.TrimStart('@');
            n = MakeSafeOracleParamName(n);

            var typeTok = FormatSqlTypeToken(m.TypeName, m.MaxLength, m.Precision, m.Scale);
            var oraType = MapSqlTypeToOracle(typeTok);

            var dir = m.IsOutput ? "OUT" : "IN";
            oracleParams.Add($"{n} {dir} {oraType}");
        }

        var nameQ = OracleIdent.QuoteIdent(funcName);

        var sb = new StringBuilder();
        sb.Append($"CREATE OR REPLACE FUNCTION {targetSchemaQ}.{nameQ}");
        if (oracleParams.Count > 0)
            sb.Append("(\n  " + string.Join(",\n  ", oracleParams.Select(p => p.Replace(" OUT ", " IN ", StringComparison.OrdinalIgnoreCase))) + "\n)");
        sb.AppendLine($" RETURN {oracleReturn} IS");
        sb.AppendLine("BEGIN");
        if (oracleReturn.StartsWith("NUMBER", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("  RETURN 0;");
        else
            sb.AppendLine("  RETURN NULL;");
        sb.AppendLine("END;");
        return sb.ToString();
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

    private static string BestEffortModuleTextTransforms(string sql)
    {
        // Order matters: strip batch noise, apply the existing conservative replacements,
        // then apply safe identifier/directive rewrites.
        var s = BasicSqlToOracleTextTransforms(StripSqlServerBatchNoise(sql));
        s = SqlScriptSanitizer.ApplyConservativeRoutineTransforms(s);
        return s;
    }

    private static async Task EnsureOracleObjectValidOrFallbackAsync(
        OracleConnection openOra,
        string owner,
        string objectName,
        string objectType,
        string? fallbackDdl,
        bool allowFallback,
        CancellationToken ct)
    {
        var status = await TryGetOracleObjectStatusAsync(openOra, owner, objectName, objectType, ct);
        if (!string.Equals(status, "INVALID", StringComparison.OrdinalIgnoreCase))
            return;

        // Pull a small, actionable compiler snippet.
        var errors = await GetOracleCompilerErrorsAsync(openOra, owner, objectName, objectType, ct);
        var snippet = errors.Count == 0 ? "(no compiler diagnostics found)" : string.Join("\n", errors.Take(12));

        if (allowFallback && !string.IsNullOrWhiteSpace(fallbackDdl))
        {
            await using (var cmd = new OracleCommand(fallbackDdl, openOra))
                await cmd.ExecuteNonQueryAsync(ct);

            var status2 = await TryGetOracleObjectStatusAsync(openOra, owner, objectName, objectType, ct);
            if (!string.Equals(status2, "INVALID", StringComparison.OrdinalIgnoreCase))
                return;

            var errors2 = await GetOracleCompilerErrorsAsync(openOra, owner, objectName, objectType, ct);
            var snippet2 = errors2.Count == 0 ? "(no compiler diagnostics found)" : string.Join("\n", errors2.Take(12));

            // Some SQL Server signatures can yield Oracle datatypes like VARCHAR2(max) or malformed NUMBER(p,s)
            // when we do best-effort mapping. If the stub itself compiles INVALID, attempt one more ultra-safe
            // fallback that simplifies sized types to their broad equivalents (e.g., VARCHAR2(n) -> CLOB,
            // NUMBER(p,s) -> NUMBER). This preserves param names/modes while avoiding parser errors.
            var simplified = SimplifyOracleTypesForStub(fallbackDdl);
            if (!string.Equals(simplified, fallbackDdl, StringComparison.Ordinal))
            {
                await using (var cmd3 = new OracleCommand(simplified, openOra))
                    await cmd3.ExecuteNonQueryAsync(ct);

                var status3 = await TryGetOracleObjectStatusAsync(openOra, owner, objectName, objectType, ct);
                if (!string.Equals(status3, "INVALID", StringComparison.OrdinalIgnoreCase))
                    return;

                var errors3 = await GetOracleCompilerErrorsAsync(openOra, owner, objectName, objectType, ct);
                var snippet3 = errors3.Count == 0 ? "(no compiler diagnostics found)" : string.Join("\n", errors3.Take(12));
                throw new InvalidOperationException($"Oracle object {owner}.{objectName} ({objectType}) compiled INVALID even after fallback stub and simplified fallback.\n{snippet3}");
            }

            throw new InvalidOperationException($"Oracle object {owner}.{objectName} ({objectType}) compiled INVALID even after fallback stub.\n{snippet2}");
        }

        throw new InvalidOperationException($"Oracle object {owner}.{objectName} ({objectType}) compiled INVALID.\n{snippet}");
    }

    private static string SimplifyOracleTypesForStub(string ddl)
    {
        if (string.IsNullOrWhiteSpace(ddl)) return ddl;

        // Keep this intentionally conservative: only simplify sized types that commonly cause parsing failures.
        var s = ddl;
        s = Regex.Replace(s, @"\bVARCHAR2\s*\(\s*[^\)]*\)", "CLOB", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bNVARCHAR2\s*\(\s*[^\)]*\)", "CLOB", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bCHAR\s*\(\s*[^\)]*\)", "CLOB", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bNCHAR\s*\(\s*[^\)]*\)", "CLOB", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bNUMBER\s*\(\s*[^\)]*\)", "NUMBER", RegexOptions.IgnoreCase);
        return s;
    }

    private static async Task<string?> TryGetOracleObjectStatusAsync(
        OracleConnection openOra,
        string owner,
        string objectName,
        string objectType,
        CancellationToken ct)
    {
        // owner is typically unquoted => stored uppercase in Oracle dictionary.
        var ownerLookup = OracleIdent.NormalizeSchemaForLookup(owner);
        var nameExact = objectName ?? string.Empty;
        var nameUpper = nameExact.ToUpperInvariant();

        const string sql = @"
SELECT status
FROM all_objects
WHERE owner = :owner
  AND object_type = :otype
  AND (object_name = :nameExact OR object_name = :nameUpper)
FETCH FIRST 1 ROWS ONLY";

        await using var cmd = new OracleCommand(sql, openOra);
        cmd.Parameters.Add(":owner", OracleDbType.Varchar2, ownerLookup, System.Data.ParameterDirection.Input);
        cmd.Parameters.Add(":otype", OracleDbType.Varchar2, objectType, System.Data.ParameterDirection.Input);
        cmd.Parameters.Add(":nameExact", OracleDbType.Varchar2, nameExact, System.Data.ParameterDirection.Input);
        cmd.Parameters.Add(":nameUpper", OracleDbType.Varchar2, nameUpper, System.Data.ParameterDirection.Input);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToString(v);
    }

    private static async Task<List<string>> GetOracleCompilerErrorsAsync(
        OracleConnection openOra,
        string owner,
        string objectName,
        string objectType,
        CancellationToken ct)
    {
        var ownerLookup = OracleIdent.NormalizeSchemaForLookup(owner);
        var nameExact = objectName ?? string.Empty;
        var nameUpper = nameExact.ToUpperInvariant();

        const string sql = @"
SELECT line, position, text
FROM all_errors
WHERE owner = :owner
  AND type = :otype
  AND (name = :nameExact OR name = :nameUpper)
ORDER BY sequence";

        var list = new List<string>();
        await using var cmd = new OracleCommand(sql, openOra);
        cmd.Parameters.Add(":owner", OracleDbType.Varchar2, ownerLookup, System.Data.ParameterDirection.Input);
        cmd.Parameters.Add(":otype", OracleDbType.Varchar2, objectType, System.Data.ParameterDirection.Input);
        cmd.Parameters.Add(":nameExact", OracleDbType.Varchar2, nameExact, System.Data.ParameterDirection.Input);
        cmd.Parameters.Add(":nameUpper", OracleDbType.Varchar2, nameUpper, System.Data.ParameterDirection.Input);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var line = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
            var pos = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
            var text = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2);
            if (list.Count < 50)
                list.Add($"{line}:{pos} {text}".Trim());
        }
        return list;
    }

    private static string BuildProcedureStubPreservingSignature(string targetSchemaQ, string procName, string? tsqlDefinition)
    {
        // Best effort: extract parameter list from the T-SQL header. If parsing fails, use empty params.
        var paramList = ExtractSqlServerParameterList(tsqlDefinition, isFunction: false);
        var oracleParams = ConvertSqlServerParamsToOracle(paramList);
        var nameQ = OracleIdent.QuoteIdent(procName);

        var sb = new StringBuilder();
        sb.Append($"CREATE OR REPLACE PROCEDURE {targetSchemaQ}.{nameQ}");
        if (oracleParams.Count > 0)
            sb.Append("(\n  " + string.Join(",\n  ", oracleParams) + "\n)");
        sb.AppendLine(" IS");
        sb.AppendLine("BEGIN");
        // Initialize OUT params to NULL so callers don't get uninitialized errors.
        foreach (var p in oracleParams.Where(p => p.Contains(" OUT ", StringComparison.OrdinalIgnoreCase)))
        {
            var pname = p.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(pname)) sb.AppendLine($"  {pname} := NULL;");
        }
        sb.AppendLine("  NULL;");
        sb.AppendLine("END;");
        return sb.ToString();
    }

    private static string BuildFunctionStubPreservingSignature(string targetSchemaQ, string funcName, string? tsqlDefinition)
    {
        var paramList = ExtractSqlServerParameterList(tsqlDefinition, isFunction: true);
        var oracleParams = ConvertSqlServerParamsToOracle(paramList);
        var returnType = ExtractSqlServerReturnType(tsqlDefinition);
        var oracleReturn = MapSqlTypeToOracle(returnType);
        var nameQ = OracleIdent.QuoteIdent(funcName);

        var sb = new StringBuilder();
        sb.Append($"CREATE OR REPLACE FUNCTION {targetSchemaQ}.{nameQ}");
        if (oracleParams.Count > 0)
            sb.Append("(\n  " + string.Join(",\n  ", oracleParams.Select(p => p.Replace(" OUT ", " IN ", StringComparison.OrdinalIgnoreCase))) + "\n)");
        sb.AppendLine($" RETURN {oracleReturn} IS");
        sb.AppendLine("BEGIN");
        // Return a NULL / 0 placeholder.
        if (oracleReturn.StartsWith("NUMBER", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("  RETURN 0;");
        else
            sb.AppendLine("  RETURN NULL;");
        sb.AppendLine("END;");
        return sb.ToString();
    }

    private static string ExtractSqlServerParameterList(string? tsql, bool isFunction)
    {
        if (string.IsNullOrWhiteSpace(tsql)) return string.Empty;

        // Grab text between object name and AS/BEGIN/RETURNS.
        // This intentionally does NOT attempt to parse nested parentheses beyond basic cases.
        var pattern = isFunction
            ? @"(?is)\bCREATE\s+(?:OR\s+ALTER\s+)?FUNCTION\s+[^\s\(]+\s*(\((?<p>[^\)]*)\))?\s*RETURNS\b"
            : @"(?is)\bCREATE\s+(?:OR\s+ALTER\s+)?PROCEDURE\s+[^\s\(]+\s*(\((?<p>[^\)]*)\))?\s*(AS|BEGIN)\b";

        var m = Regex.Match(tsql, pattern);
        if (!m.Success) return string.Empty;
        return m.Groups["p"].Success ? m.Groups["p"].Value : string.Empty;
    }

    private static string ExtractSqlServerReturnType(string? tsql)
    {
        if (string.IsNullOrWhiteSpace(tsql)) return "NUMBER";
        var m = Regex.Match(tsql, @"(?is)\bRETURNS\s+(?<t>[^\s\(]+(?:\s*\([^\)]*\))?)");
        return m.Success ? m.Groups["t"].Value.Trim() : "NUMBER";
    }

    private static List<string> ConvertSqlServerParamsToOracle(string paramList)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(paramList)) return result;

        // Split on commas - AdventureWorks-style headers are simple enough for this best-effort conversion.
        foreach (var raw in paramList.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            // Example: @StartProductID int, @CheckDate datetime OUTPUT, @Msg nvarchar(2048) = NULL
            var tokens = Regex.Split(part, @"\s+").Where(t => t.Length > 0).ToList();
            if (tokens.Count < 2) continue;

            var nameTok = tokens[0].Trim();
            var name = nameTok.TrimStart('@');
            name = MakeSafeOracleParamName(name);

            var typeTok = tokens[1].Trim();
            // Merge type token with length/precision if separated (e.g., varchar (50) )
            if (tokens.Count > 2 && tokens[2].StartsWith("(") && !typeTok.Contains('('))
                typeTok += tokens[2];

            var dir = part.IndexOf("OUTPUT", StringComparison.OrdinalIgnoreCase) >= 0 ? "OUT" : "IN";
            var oraType = MapSqlTypeToOracle(typeTok);

            result.Add($"{name} {dir} {oraType}");
        }
        return result;
    }

    private static readonly HashSet<string> OracleReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Minimal, high-impact set for parameter names. (Oracle has many reserved words; this list focuses on common collisions.)
        "DATE","NUMBER","VARCHAR2","CHAR","NCHAR","NVARCHAR2","CLOB","BLOB","RAW","LONG","TIMESTAMP","INTERVAL",
        "SELECT","FROM","WHERE","GROUP","ORDER","BY","HAVING","JOIN","INNER","LEFT","RIGHT","FULL","ON","INTO",
        "CREATE","OR","REPLACE","ALTER","DROP","TRUNCATE",
        "FUNCTION","PROCEDURE","PACKAGE","TRIGGER","VIEW","TABLE","INDEX","SEQUENCE","TYPE",
        "BEGIN","END","IS","AS","DECLARE","RETURN",
        "IN","OUT","INOUT",
        "NULL","DEFAULT","VALUES",
        "WHEN","THEN","ELSE","LOOP","FOR","WHILE","IF","CASE",
        "PRIMARY","KEY","CONSTRAINT","REFERENCES","UNIQUE","CHECK"
    };

    private static string MakeSafeOracleParamName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "p";

        // Build a safe identifier (letters/digits/_), and ensure it starts with a letter/_.
        var sb = new StringBuilder(name.Length + 2);
        if (!char.IsLetter(name[0]) && name[0] != '_') sb.Append('p').Append('_');
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            else sb.Append('_');
        }

        var candidate = sb.ToString();
        if (candidate.Length == 0) candidate = "p";

        // Avoid Oracle reserved words (e.g., parameter "@date" => "date" which collides with DATE type).
        if (OracleReservedWords.Contains(candidate))
            candidate = "p_" + candidate;

        return candidate;
    }

    private static string MapSqlTypeToOracle(string? sqlType)
    {
        var t = (sqlType ?? string.Empty).Trim().ToLowerInvariant();
        if (t.Length == 0) return "VARCHAR2(4000)";

        // strip identity/default noise
        t = t.Replace("identity", "");
        if (t.StartsWith("int")) return "NUMBER";
        if (t.StartsWith("bigint")) return "NUMBER(19)";
        if (t.StartsWith("smallint")) return "NUMBER(5)";
        if (t.StartsWith("tinyint")) return "NUMBER(3)";
        if (t.StartsWith("bit")) return "NUMBER(1)";
        if (t.StartsWith("decimal") || t.StartsWith("numeric"))
        {
            // keep precision if present
            var m = Regex.Match(t, @"\((?<p>[^\)]*)\)");
            return m.Success ? $"NUMBER({m.Groups["p"].Value})" : "NUMBER";
        }
        if (t.StartsWith("float") || t.StartsWith("real")) return "BINARY_DOUBLE";
        if (t.StartsWith("money") || t.StartsWith("smallmoney")) return "NUMBER(19,4)";
        if (t.StartsWith("date") || t.StartsWith("datetime") || t.StartsWith("smalldatetime")) return "DATE";
        if (t.StartsWith("datetime2") || t.StartsWith("datetimeoffset")) return "TIMESTAMP";
        if (t.StartsWith("time")) return "INTERVAL DAY TO SECOND";
        if (t.StartsWith("uniqueidentifier")) return "VARCHAR2(36)";
        if (t.StartsWith("varbinary") || t.StartsWith("binary") || t.StartsWith("image")) return "BLOB";
        if (t.StartsWith("xml")) return "CLOB";
        if (t.StartsWith("nchar") || t.StartsWith("nvarchar") || t.StartsWith("char") || t.StartsWith("varchar"))
        {
            var m = Regex.Match(t, @"\((?<n>[^\)]*)\)");
            var nRaw = m.Success ? m.Groups["n"].Value.Trim() : "4000";

            // SQL Server (n)varchar(max) has no direct VARCHAR2 equivalent in Oracle SQL.
            // Using CLOB here ensures the generated stub compiles and is a safer semantic match.
            if (nRaw.Equals("max", StringComparison.OrdinalIgnoreCase))
                return "CLOB";

            // If the length is greater than 4000, prefer CLOB to avoid generating invalid VARCHAR2(n).
            if (int.TryParse(nRaw, out var nVal) && nVal > 4000)
                return "CLOB";

            // Cap to 4000 for VARCHAR2 in SQL (Oracle supports more with MAX_STRING_SIZE=EXTENDED)
            var n = int.TryParse(nRaw, out var parsed) ? Math.Max(1, Math.Min(4000, parsed)).ToString() : "4000";
            return $"VARCHAR2({n})";
        }
        return "VARCHAR2(4000)";
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

        // Attempt best-effort transform; if compilation still fails, create a stub view.
        var text = BestEffortModuleTextTransforms(def);
        // Replace CREATE VIEW with CREATE OR REPLACE VIEW and schema qualification.
        text = ReplaceCreateHeader(text, "VIEW", schema, viewName, schemaQ, nameQ);

        var stub = $"CREATE OR REPLACE VIEW {schemaQ}.{nameQ} AS SELECT CAST(NULL AS NUMBER) AS DUMMY FROM DUAL WHERE 1=0";
        await ExecuteOracleWithFallbackAsync(openOra, text, stub, allowStub, ct);
        await EnsureOracleObjectValidOrFallbackAsync(openOra, targetSchema, viewName, "VIEW", stub, allowStub, ct);
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

        var text = BestEffortModuleTextTransforms(def);
        text = ReplaceCreateHeader(text, "PROCEDURE", schema, procName, schemaQ, nameQ);

        var stub = BuildProcedureStubPreservingSignature(schemaQ, procName, def);
        await ExecuteOracleWithFallbackAsync(openOra, text, stub, allowStub, ct);
        await EnsureOracleObjectValidOrFallbackAsync(openOra, targetSchema, procName, "PROCEDURE", stub, allowStub, ct);
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

        var text = BestEffortModuleTextTransforms(def);
        text = ReplaceCreateHeader(text, "FUNCTION", schema, funcName, schemaQ, nameQ);

        var stub = await BuildFunctionStubFromSqlMetadataAsync(openSql, dbName, schema, funcName, schemaQ, def, ct);
        await ExecuteOracleWithFallbackAsync(openOra, text, stub, allowStub, ct);
        await EnsureOracleObjectValidOrFallbackAsync(openOra, targetSchema, funcName, "FUNCTION", stub, allowStub, ct);
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
        var text = BestEffortModuleTextTransforms(def);
        text = ReplaceCreateHeader(text, "TRIGGER", triggerSchema, triggerName, trgSchemaQ, trgNameQ);

        await ExecuteOracleWithFallbackAsync(openOra, text, stub, allowStub, ct);
        await EnsureOracleObjectValidOrFallbackAsync(openOra, parentTargetSchema, triggerName, "TRIGGER", stub, allowStub, ct);
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
