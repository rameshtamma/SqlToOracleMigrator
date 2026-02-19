using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core.Validation;

/// <summary>
/// Post-migration validator: compares SQL Server vs Oracle for object existence, status, and basic metrics.
/// Designed to be invoked by the Desktop UI.
/// </summary>
public sealed class PostMigrationValidator
{
    private readonly IAppLogger _logger;

    public PostMigrationValidator(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<PostMigrationValidationReport> ValidateAsync(
        SqlConnection openSql,
        string sourceDatabase,
        OracleConnection openOra,
        IReadOnlyCollection<string> schemas,
        PostMigrationValidationOptions options,
        CancellationToken ct)
    {
        if (schemas.Count == 0) throw new ArgumentException("schemas must not be empty", nameof(schemas));

        var started = DateTimeOffset.UtcNow;
        var report = new PostMigrationValidationReport
        {
            StartedUtc = started,
            SourceDatabase = sourceDatabase,
            TargetDatabase = await GetOracleCurrentContainerAsync(openOra, ct),
            Schemas = schemas.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            Options = options,
        };

        _logger.Info($"[PostValidation] Starting post-migration validation. SQL DB='{sourceDatabase}', ORA='{report.TargetDatabase}', Schemas={schemas.Count}.");

        // Inventory
        var src = await GetSqlInventoryAsync(openSql, sourceDatabase, schemas, ct);
        var tgt = await GetOracleInventoryAsync(openOra, schemas, ct);

        report.SourceInventory = src;
        report.TargetInventory = tgt;

        // Compare object existence and status
        CompareInventories(report);

        // Compare row counts for tables
        if (options.IncludeRowCounts)
        {
            await CompareRowCountsAsync(openSql, sourceDatabase, openOra, schemas, report, options, ct);
        }

        // Compare PK presence and invalid objects
        if (options.IncludeKeyAndInvalidChecks)
        {
            await CompareKeysAndInvalidObjectsAsync(openSql, sourceDatabase, openOra, schemas, report, ct);
        }

        report.CompletedUtc = DateTimeOffset.UtcNow;
        report.DurationMs = (long)(report.CompletedUtc.Value - started).TotalMilliseconds;
        report.Summarize();

        _logger.Info($"[PostValidation] Completed validation in {report.DurationMs}ms. Issues={report.Issues.Count}.");
        return report;
    }

    public static async Task<string> SaveReportAsync(PostMigrationValidationReport report, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var jsonPath = Path.Combine(outputDir, $"PostMigrationValidation_{ts}.json");
        var htmlPath = Path.Combine(outputDir, $"PostMigrationValidation_{ts}.html");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json, ct);
        await File.WriteAllTextAsync(htmlPath, report.ToHtml(), ct);

        return jsonPath;
    }

    private static async Task<string> GetOracleCurrentContainerAsync(OracleConnection openOra, CancellationToken ct)
    {
        await using var cmd = new OracleCommand("SELECT SYS_CONTEXT('USERENV','CON_NAME') FROM dual", openOra);
        var v = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToString(v) ?? "(unknown)";
    }

    private static async Task<DbInventorySnapshot> GetSqlInventoryAsync(SqlConnection openSql, string db, IReadOnlyCollection<string> schemas, CancellationToken ct)
    {
        var snap = new DbInventorySnapshot { Engine = "SQLServer" };
        var schemaList = schemas.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

        // Objects
        // Filter out Microsoft shipped + replication/system artifacts so validation focuses on user objects.
        var sql = @"
USE [__DB__];
SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type
FROM sys.objects o
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name IN (__SCHEMAS__)
  AND o.is_ms_shipped = 0
  AND o.type IN ('U','V','P','FN','TF','IF','TR','SN','SO')
  AND o.name NOT LIKE 'sp_MS%'
  AND o.name NOT LIKE 'spt_%'
  AND o.name NOT LIKE 'MSreplication_%'
ORDER BY s.name, o.type, o.name;";

        sql = sql.Replace("__DB__", db.Replace("]", "]]"));
        sql = sql.Replace("__SCHEMAS__", string.Join(",", schemaList.Select((_, i) => $"@s{i}")));

        await using (var cmd = new SqlCommand(sql, openSql))
        {
            for (var i = 0; i < schemaList.Length; i++)
                cmd.Parameters.AddWithValue($"@s{i}", schemaList[i]);

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var schema = rdr.GetString(0);
                var name = rdr.GetString(1);
                var type = rdr.GetString(2);
                snap.Objects.Add(new DbObjectRef(schema, name, MapSqlType(type), Status: "VALID"));
            }
        }

        // User-defined data types (UDTs) live in sys.types, not sys.objects.
        var udtSql = @"
USE [__DB__];
SELECT s.name AS schema_name, t.name AS type_name
FROM sys.types t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name IN (__SCHEMAS__)
  AND t.is_user_defined = 1
ORDER BY s.name, t.name;";

        udtSql = udtSql.Replace("__DB__", db.Replace("]", "]]"));
        udtSql = udtSql.Replace("__SCHEMAS__", string.Join(",", schemaList.Select((_, i) => $"@u{i}")));

        await using (var cmd = new SqlCommand(udtSql, openSql))
        {
            for (var i = 0; i < schemaList.Length; i++)
                cmd.Parameters.AddWithValue($"@u{i}", schemaList[i]);

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var schema = rdr.GetString(0);
                var name = rdr.GetString(1);
                snap.Objects.Add(new DbObjectRef(schema, name, "TYPE", Status: "VALID"));
            }
        }

        // Row counts (fast, based on partitions)
        var rowSql = @"
USE [__DB__];
SELECT s.name AS schema_name, t.name AS table_name, SUM(p.rows) AS row_count
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id
JOIN sys.partitions p ON p.object_id = t.object_id
WHERE s.name IN (__SCHEMAS__)
  AND p.index_id IN (0,1)
GROUP BY s.name, t.name
ORDER BY s.name, t.name;";

        rowSql = rowSql.Replace("__DB__", db.Replace("]", "]]"));
        rowSql = rowSql.Replace("__SCHEMAS__", string.Join(",", schemaList.Select((_, i) => $"@rs{i}")));

        await using (var cmd = new SqlCommand(rowSql, openSql))
        {
            for (var i = 0; i < schemaList.Length; i++)
                cmd.Parameters.AddWithValue($"@rs{i}", schemaList[i]);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                snap.TableRowCounts[$"{rdr.GetString(0)}.{rdr.GetString(1)}"] = rdr.IsDBNull(2) ? 0 : Convert.ToInt64(rdr.GetValue(2));
            }
        }

        return snap;
    }

    private static async Task<DbInventorySnapshot> GetOracleInventoryAsync(OracleConnection openOra, IReadOnlyCollection<string> schemas, CancellationToken ct)
    {
        var snap = new DbInventorySnapshot { Engine = "Oracle" };
        var schemaList = schemas.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).ToArray();

        // Objects + status
        var inList = string.Join(",", schemaList.Select((_, i) => $":s{i}"));
        var sql = $@"
SELECT owner, object_name, object_type, status
FROM all_objects
WHERE owner IN ({inList})
  AND object_type IN ('TABLE','VIEW','PROCEDURE','FUNCTION','TRIGGER','SYNONYM','SEQUENCE','TYPE')
ORDER BY owner, object_type, object_name";

        await using (var cmd = new OracleCommand(sql, openOra))
        {
            for (var i = 0; i < schemaList.Length; i++)
                cmd.Parameters.Add($":s{i}", OracleDbType.Varchar2, schemaList[i], System.Data.ParameterDirection.Input);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var owner = rdr.GetString(0);
                var name = rdr.GetString(1);
                var type = rdr.GetString(2);
                var status = rdr.IsDBNull(3) ? "UNKNOWN" : rdr.GetString(3);
                snap.Objects.Add(new DbObjectRef(owner, name, type, status));
            }
        }

        return snap;
    }

    private static string MapSqlType(string sqlType)
    {
        var t = (sqlType ?? string.Empty).Trim();
        return t switch
        {
            "U" => "TABLE",
            "V" => "VIEW",
            "P" => "PROCEDURE",
            "FN" => "FUNCTION",
            "TF" => "FUNCTION",
            "IF" => "FUNCTION",
            "TR" => "TRIGGER",
            "SN" => "SYNONYM",
            "SO" => "SEQUENCE",
            _ => t
        };
    }

    private static void CompareInventories(PostMigrationValidationReport report)
    {
        var srcSet = report.SourceInventory.Objects
            .Select(o => o.ToKey())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tgtSet = report.TargetInventory.Objects
            .Select(o => o.ToKey())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var o in report.SourceInventory.Objects)
        {
            if (!tgtSet.Contains(o.ToKey()))
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Category = "MissingObject",
                    Schema = o.Schema,
                    Name = o.Name,
                    ObjectType = o.ObjectType,
                    Message = "Object exists in SQL Server but was not found in Oracle target."
                });
            }
        }

        foreach (var o in report.TargetInventory.Objects)
        {
            if (!srcSet.Contains(o.ToKey()))
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warn,
                    Category = "ExtraObject",
                    Schema = o.Schema,
                    Name = o.Name,
                    ObjectType = o.ObjectType,
                    Message = "Object exists in Oracle target but was not found in SQL Server source."
                });
            }
        }

        // Invalid objects
        foreach (var o in report.TargetInventory.Objects)
        {
            if (string.Equals(o.Status, "INVALID", StringComparison.OrdinalIgnoreCase))
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Category = "InvalidObject",
                    Schema = o.Schema,
                    Name = o.Name,
                    ObjectType = o.ObjectType,
                    Message = "Object is INVALID in Oracle (compile errors)."
                });
            }
        }
    }

    private static async Task CompareRowCountsAsync(
        SqlConnection openSql,
        string db,
        OracleConnection openOra,
        IReadOnlyCollection<string> schemas,
        PostMigrationValidationReport report,
        PostMigrationValidationOptions options,
        CancellationToken ct)
    {
        // For Oracle, we compute exact counts (can be expensive). Parallelism is bounded.
        var tableKeys = report.SourceInventory.Objects
            .Where(o => o.ObjectType == "TABLE")
            .Select(o => $"{o.Schema}.{o.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gate = new SemaphoreSlim(Math.Max(1, options.RowCountParallelism));
        var results = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var resolved = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Candidate Oracle owners: user supplied schemas (upper), current user, and SYS (some migrations run as SYS).
        var ownerCandidates = new List<string>();
        ownerCandidates.AddRange(schemas.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0));
        try
        {
            await using var ucmd = new OracleCommand("SELECT USER FROM dual", openOra);
            var user = Convert.ToString(await ucmd.ExecuteScalarAsync(ct));
            if (!string.IsNullOrWhiteSpace(user)) ownerCandidates.Add(user!.Trim().ToUpperInvariant());
        }
        catch { /* best-effort */ }
        if (!ownerCandidates.Contains("SYS", StringComparer.OrdinalIgnoreCase)) ownerCandidates.Add("SYS");
        ownerCandidates = ownerCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tasks = tableKeys.Select(async key =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var parts = key.Split('.', 2);
                var schema = parts[0];
                var name = parts[1];
                // Resolve actual owner/table-name in Oracle (schema mapping might differ; identifiers might be normalized).
                var resolvedOra = await ResolveOracleTableAsync(openOra, name, ownerCandidates, ct);
                if (resolvedOra is null)
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warn,
                        Category = "RowCount",
                        Schema = schema,
                        Name = name,
                        ObjectType = "TABLE",
                        Message = "Failed to count Oracle rows: table not found in Oracle (owner/schema mapping mismatch)."
                    });
                    return;
                }

                resolved[key] = $"{resolvedOra.Value.Owner}.{resolvedOra.Value.TableName}";
                var ownerQ = OracleIdent.FormatSchema(resolvedOra.Value.Owner);
                // TableName from ALL_TABLES is already normalized to Oracle's storage name.
                var tableQ = OracleIdent.FormatObject(resolvedOra.Value.TableName, preferUnquotedUppercase: true);
                var sql = $"SELECT COUNT(*) FROM {ownerQ}.{tableQ}";

                await using var cmd = new OracleCommand(sql, openOra);
                cmd.CommandTimeout = Math.Max(30, options.RowCountCommandTimeoutSeconds);
                var v = await cmd.ExecuteScalarAsync(ct);
                results[key] = Convert.ToInt64(v);
            }
            catch (Exception ex)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warn,
                    Category = "RowCount",
                    Schema = key.Split('.', 2)[0],
                    Name = key.Split('.', 2)[1],
                    ObjectType = "TABLE",
                    Message = $"Failed to count Oracle rows: {ex.Message}"
                });
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        report.TargetInventory.TableRowCounts = results.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        report.ResolvedOracleTables = resolved.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in report.SourceInventory.TableRowCounts)
        {
            if (report.TargetInventory.TableRowCounts.TryGetValue(kvp.Key, out var tgtCount))
            {
                if (kvp.Value != tgtCount)
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Error,
                        Category = "RowCountMismatch",
                        Schema = kvp.Key.Split('.', 2)[0],
                        Name = kvp.Key.Split('.', 2)[1],
                        ObjectType = "TABLE",
                        Message = $"Row count mismatch. SQL={kvp.Value}, ORA={tgtCount}."
                    });
                }
            }
        }
    }

    private static async Task<(string Owner, string TableName)?> ResolveOracleTableAsync(
        OracleConnection openOra,
        string sourceTableName,
        IReadOnlyList<string> ownerCandidates,
        CancellationToken ct)
    {
        var t = sourceTableName.Trim();
        if (t.Length == 0) return null;

        // Oracle stores unquoted identifiers as uppercase. If the table was created quoted, ALL_TABLES stores the exact name.
        // We try uppercase first, then original.
        var namesToTry = new[] { t.ToUpperInvariant(), t };

        foreach (var tableName in namesToTry.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // 1) Preferred: find in candidate owners.
            if (ownerCandidates.Count > 0)
            {
                var inOwners = string.Join(",", ownerCandidates.Select((_, i) => $":o{i}"));
                var sql = $@"
SELECT owner, table_name
FROM all_tables
WHERE table_name = :t
  AND owner IN ({inOwners})
ORDER BY CASE WHEN owner = :pref THEN 0 ELSE 1 END, owner";
                await using var cmd = new OracleCommand(sql, openOra);
                cmd.Parameters.Add(":t", OracleDbType.Varchar2, tableName, System.Data.ParameterDirection.Input);
                for (var i = 0; i < ownerCandidates.Count; i++)
                    cmd.Parameters.Add($":o{i}", OracleDbType.Varchar2, ownerCandidates[i], System.Data.ParameterDirection.Input);
                cmd.Parameters.Add(":pref", OracleDbType.Varchar2, ownerCandidates[0], System.Data.ParameterDirection.Input);

                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                if (await rdr.ReadAsync(ct))
                    return (rdr.GetString(0), rdr.GetString(1));
            }

            // 2) Fallback: find in any owner (helps when schema mapping is unknown).
            await using (var cmd2 = new OracleCommand("SELECT owner, table_name FROM all_tables WHERE table_name = :t ORDER BY owner", openOra))
            {
                cmd2.Parameters.Add(":t", OracleDbType.Varchar2, tableName, System.Data.ParameterDirection.Input);
                await using var rdr2 = await cmd2.ExecuteReaderAsync(ct);
                if (await rdr2.ReadAsync(ct))
                    return (rdr2.GetString(0), rdr2.GetString(1));
            }
        }

        return null;
    }

    private static async Task CompareKeysAndInvalidObjectsAsync(
        SqlConnection openSql,
        string db,
        OracleConnection openOra,
        IReadOnlyCollection<string> schemas,
        PostMigrationValidationReport report,
        CancellationToken ct)
    {
        // Basic check: SQL tables that have PK should also have PK in Oracle.
        var schemaList = schemas.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        var pkSql = @"
USE [__DB__];
SELECT s.name AS schema_name, t.name AS table_name
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name IN (__SCHEMAS__)
  AND EXISTS (
    SELECT 1
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = t.object_id AND kc.type = 'PK'
  )";
        pkSql = pkSql.Replace("__DB__", db.Replace("]", "]]"));
        pkSql = pkSql.Replace("__SCHEMAS__", string.Join(",", schemaList.Select((_, i) => $"@p{i}")));

        var sqlPkTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(pkSql, openSql))
        {
            for (var i = 0; i < schemaList.Length; i++) cmd.Parameters.AddWithValue($"@p{i}", schemaList[i]);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                sqlPkTables.Add($"{rdr.GetString(0)}.{rdr.GetString(1)}");
            }
        }

        // Oracle PK tables
        var oraOwners = schemas.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).ToArray();
        var inOwners = string.Join(",", oraOwners.Select((_, i) => $":o{i}"));
        var oraPkSql = $@"
SELECT owner, table_name
FROM all_constraints
WHERE owner IN ({inOwners})
  AND constraint_type = 'P'";

        var oraPkTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new OracleCommand(oraPkSql, openOra))
        {
            for (var i = 0; i < oraOwners.Length; i++)
                cmd.Parameters.Add($":o{i}", OracleDbType.Varchar2, oraOwners[i], System.Data.ParameterDirection.Input);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                oraPkTables.Add($"{rdr.GetString(0)}.{rdr.GetString(1)}");
            }
        }

        foreach (var t in sqlPkTables)
        {
            // SQL schema names might differ in casing; oracle owners are upper.
            var parts = t.Split('.', 2);
            var oraKey = $"{parts[0].ToUpperInvariant()}.{parts[1].ToUpperInvariant()}";
            if (!oraPkTables.Contains(oraKey))
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Category = "MissingPrimaryKey",
                    Schema = parts[0],
                    Name = parts[1],
                    ObjectType = "TABLE",
                    Message = "Table has a primary key in SQL Server but no primary key constraint found in Oracle."
                });
            }
        }

        // Oracle compile errors
        var errSql = $@"
SELECT owner, name, type, line, position, text
FROM all_errors
WHERE owner IN ({inOwners})
ORDER BY owner, name, sequence";

        await using (var cmd = new OracleCommand(errSql, openOra))
        {
            for (var i = 0; i < oraOwners.Length; i++)
                cmd.Parameters.Add($":o{i}", OracleDbType.Varchar2, oraOwners[i], System.Data.ParameterDirection.Input);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            while (await rdr.ReadAsync(ct))
            {
                var owner = rdr.GetString(0);
                var name = rdr.GetString(1);
                var type = rdr.GetString(2);
                var line = rdr.GetInt32(3);
                var pos = rdr.GetInt32(4);
                var text = rdr.GetString(5);
                var key = $"{owner}.{name}|{type}";
                if (!grouped.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    grouped[key] = list;
                }
                if (list.Count < 20)
                    list.Add($"{line}:{pos} {text}");
            }

            foreach (var kvp in grouped)
            {
                var parts = kvp.Key.Split('|');
                var obj = parts[0];
                var type = parts.Length > 1 ? parts[1] : "";
                var dot = obj.IndexOf('.');
                var owner = dot > 0 ? obj.Substring(0, dot) : obj;
                var name = dot > 0 ? obj.Substring(dot + 1) : obj;
                report.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Category = "OracleCompileErrors",
                    Schema = owner,
                    Name = name,
                    ObjectType = type,
                    Message = "Oracle reported compile errors (see Details).",
                    Details = string.Join("\n", kvp.Value)
                });
            }
        }
    }
}

public sealed class PostMigrationValidationOptions
{
    public bool IncludeRowCounts { get; set; } = true;
    public bool IncludeKeyAndInvalidChecks { get; set; } = true;
    public int RowCountParallelism { get; set; } = 4;
    public int RowCountCommandTimeoutSeconds { get; set; } = 120;
}

public sealed class PostMigrationValidationReport
{
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public long? DurationMs { get; set; }
    public string SourceDatabase { get; set; } = "";
    public string TargetDatabase { get; set; } = "";
    public List<string> Schemas { get; set; } = new();
    public PostMigrationValidationOptions Options { get; set; } = new();

    public DbInventorySnapshot SourceInventory { get; set; } = new();
    public DbInventorySnapshot TargetInventory { get; set; } = new();

    /// <summary>
    /// For each source table key (schema.table), the resolved Oracle owner/table name used for counting.
    /// Helps end users understand schema mapping without querying ALL_TABLES manually.
    /// </summary>
    public Dictionary<string, string> ResolvedOracleTables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ValidationIssue> Issues { get; set; } = new();

    public ValidationSummary Summary { get; set; } = new();

    public void Summarize()
    {
        Summary = new ValidationSummary
        {
            SourceObjectCount = SourceInventory.Objects.Count,
            TargetObjectCount = TargetInventory.Objects.Count,
            ErrorCount = Issues.Count(i => i.Severity == ValidationSeverity.Error),
            WarnCount = Issues.Count(i => i.Severity == ValidationSeverity.Warn)
        };
    }

    public string ToHtml()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<html><head><meta charset='utf-8'><style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:20px;} table{border-collapse:collapse;width:100%;} th,td{border:1px solid #ddd;padding:8px;} th{background:#f3f3f3;text-align:left;} .err{color:#b00020;font-weight:600;} .warn{color:#a05a00;font-weight:600;}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h2>Post Migration Validation Report</h2>");
        sb.AppendLine($"<p><b>SQL DB:</b> {System.Net.WebUtility.HtmlEncode(SourceDatabase)}<br/><b>Oracle:</b> {System.Net.WebUtility.HtmlEncode(TargetDatabase)}<br/><b>Schemas:</b> {System.Net.WebUtility.HtmlEncode(string.Join(", ", Schemas))}<br/><b>Started:</b> {StartedUtc:u}<br/><b>Completed:</b> {CompletedUtc:u}</p>");
        sb.AppendLine($"<p><b>Summary:</b> SQL Objects={Summary.SourceObjectCount}, ORA Objects={Summary.TargetObjectCount}, Errors={Summary.ErrorCount}, Warnings={Summary.WarnCount}</p>");

        // Checks summary (so the UI/report can show "success" with actual values)
        sb.AppendLine("<h3>Checks</h3>");
        sb.AppendLine("<table><tr><th>Check</th><th>Status</th><th>Details</th></tr>");
        var anyErrors = Summary.ErrorCount > 0;
        var anyWarns = Summary.WarnCount > 0;
        sb.AppendLine($"<tr><td>Inventory</td><td class='{(anyErrors ? "err" : "")}'>{(anyErrors ? "Review" : "OK")}</td><td>SQL Objects={Summary.SourceObjectCount}, Oracle Objects={Summary.TargetObjectCount}</td></tr>");
        sb.AppendLine($"<tr><td>Row counts</td><td class='{(Issues.Any(i => i.Category.StartsWith("RowCount", StringComparison.OrdinalIgnoreCase)) ? "warn" : "")}'>{(Options.IncludeRowCounts ? (Issues.Any(i => i.Category.StartsWith("RowCount", StringComparison.OrdinalIgnoreCase)) ? "Partial" : "OK") : "Skipped")}</td><td>Compared tables={SourceInventory.TableRowCounts.Count}, Oracle counts={(TargetInventory.TableRowCounts?.Count ?? 0)}</td></tr>");
        sb.AppendLine($"<tr><td>Keys/Invalid objects</td><td class='{(Issues.Any(i => i.Category.Contains("PrimaryKey", StringComparison.OrdinalIgnoreCase) || i.Category.Contains("Invalid", StringComparison.OrdinalIgnoreCase) || i.Category.Contains("Compile", StringComparison.OrdinalIgnoreCase)) ? "warn" : "")}'>{(Options.IncludeKeyAndInvalidChecks ? (Issues.Any(i => i.Category.Contains("PrimaryKey", StringComparison.OrdinalIgnoreCase) || i.Category.Contains("Invalid", StringComparison.OrdinalIgnoreCase) || i.Category.Contains("Compile", StringComparison.OrdinalIgnoreCase)) ? "Review" : "OK") : "Skipped")}</td><td>PK/invalid checks enabled={Options.IncludeKeyAndInvalidChecks}</td></tr>");
        sb.AppendLine("</table>");

        // Row count details
        if (Options.IncludeRowCounts && SourceInventory.TableRowCounts.Count > 0)
        {
            sb.AppendLine("<h3>Row Count Comparison</h3>");
            sb.AppendLine("<table><tr><th>Table</th><th>SQL Rows</th><th>Oracle Table</th><th>Oracle Rows</th><th>Delta</th><th>Status</th></tr>");
            foreach (var kvp in SourceInventory.TableRowCounts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var key = kvp.Key;
                var sqlRows = kvp.Value;
                var oraRows = TargetInventory.TableRowCounts.TryGetValue(key, out var v) ? (long?)v : null;
                var oraName = ResolvedOracleTables.TryGetValue(key, out var r) ? r : "(unresolved)";
                var delta = oraRows.HasValue ? (oraRows.Value - sqlRows) : (long?)null;
                var status = oraRows.HasValue ? (delta == 0 ? "OK" : "Mismatch") : "Unavailable";
                var cls = status == "Mismatch" ? "err" : (status == "Unavailable" ? "warn" : "");
                sb.AppendLine($"<tr><td>{System.Net.WebUtility.HtmlEncode(key)}</td><td>{sqlRows}</td><td>{System.Net.WebUtility.HtmlEncode(oraName)}</td><td>{(oraRows.HasValue ? oraRows.Value.ToString() : "-")}</td><td>{(delta.HasValue ? delta.Value.ToString() : "-")}</td><td class='{cls}'>{status}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<h3>Issues</h3>");
        sb.AppendLine("<table><tr><th>Severity</th><th>Category</th><th>Schema</th><th>Name</th><th>Type</th><th>Message</th></tr>");
        foreach (var i in Issues)
        {
            var cls = i.Severity == ValidationSeverity.Error ? "err" : "warn";
            sb.AppendLine($"<tr><td class='{cls}'>{i.Severity}</td><td>{System.Net.WebUtility.HtmlEncode(i.Category)}</td><td>{System.Net.WebUtility.HtmlEncode(i.Schema)}</td><td>{System.Net.WebUtility.HtmlEncode(i.Name)}</td><td>{System.Net.WebUtility.HtmlEncode(i.ObjectType)}</td><td>{System.Net.WebUtility.HtmlEncode(i.Message)}</td></tr>");
        }
        sb.AppendLine("</table>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}

public sealed class ValidationSummary
{
    public int SourceObjectCount { get; set; }
    public int TargetObjectCount { get; set; }
    public int ErrorCount { get; set; }
    public int WarnCount { get; set; }
}

public sealed class DbInventorySnapshot
{
    public string Engine { get; set; } = "";
    public List<DbObjectRef> Objects { get; set; } = new();
    public Dictionary<string, long> TableRowCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DbObjectRef(string Schema, string Name, string ObjectType, string Status)
{
    public string ToKey() => $"{Schema}.{Name}|{ObjectType}";
}

public enum ValidationSeverity
{
    Warn,
    Error
}

public sealed class ValidationIssue
{
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.Error;
    public string Category { get; set; } = "";
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Details { get; set; }
}
