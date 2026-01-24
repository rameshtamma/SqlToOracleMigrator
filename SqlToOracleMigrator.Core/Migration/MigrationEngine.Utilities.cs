using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using SqlToOracleMigrator.Core.Migration;
using SqlToOracleMigrator.Core.Tracking;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

using System.Threading;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{

// Gates used when we must fall back to shared connections (e.g., password is not available for child connections).
private static readonly SemaphoreSlim _sharedSqlGate = new(1, 1);
private static readonly SemaphoreSlim _sharedOraGate = new(1, 1);


private void ValidateResumeCompatibility(ToolMigRunInfo run, string currentRequestJson)
    {
        if (string.IsNullOrWhiteSpace(run.RequestJson))
            return;

        try
        {
            using var prior = JsonDocument.Parse(run.RequestJson);
            using var curr = JsonDocument.Parse(currentRequestJson);

            // Best-effort check: warn on mismatched critical flags.
            string? Get(JsonDocument d, string prop)
            {
                if (d.RootElement.TryGetProperty(prop, out var p))
                    return p.ToString();
                return null;
            }

            var pairs = new[]
            {
                ("SourceDatabase", "SourceDatabase"),
                ("CloneSourceSchemas", "CloneSourceSchemas"),
                ("AutoCreateTargetSchemas", "AutoCreateTargetSchemas"),
                ("EnableDataDefValidation", "EnableDataDefValidation"),
                ("EnableDataValidation", "EnableDataValidation"),
                ("DataValidationRowLimit", "DataValidationRowLimit"),
                ("ValidateFullDataset", "ValidateFullDataset")
            };

            foreach (var (p, c) in pairs)
            {
                var v1 = Get(prior, p);
                var v2 = Get(curr, c);
                if (v1 is null || v2 is null) continue;
                if (!string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase))
                    _logger.Warn($"[ToolMig] Resume: request option '{p}' differs (prior={v1}, current={v2}). Resume will proceed, but results may be inconsistent.");
            }
        }
        catch
        {
            // ignore parse issues
        }
    }

private void WriteMasterTemplateArtifacts(ToolMigRunInfo run, MigrationRequest request, string requestJson, string runDir, Action<string> appendLog)
    {
        try
        {
            // Always ensure convert log exists.
            File.AppendAllText(Path.Combine(runDir, "Convert_ToOracle.log"), $"{DateTimeOffset.Now:O} [Run] RunId={run.RunId} Version={run.Version} SourceDb={run.SourceDatabase}{Environment.NewLine}");

            var templatePath = Path.Combine(_paths.TemplatesDirectory, "DB_Conversion_MasterTemplate_v1.txt");
            if (!File.Exists(templatePath))
            {
                appendLog("[Template] DB_Conversion_MasterTemplate_v1.txt not found; skipping master script generation.");
                return;
            }

            var template = File.ReadAllText(templatePath);
            var rendered = template
                .Replace("{{RUN_ID}}", run.RunId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                .Replace("{{RUN_VERSION}}", run.Version.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{SOURCE_DB}}", request.SourceDatabase, StringComparison.OrdinalIgnoreCase)
                .Replace("{{TARGET_SCHEMA}}", request.TargetSchema, StringComparison.OrdinalIgnoreCase)
                .Replace("{{CLONE_SOURCE_SCHEMAS}}", request.CloneSourceSchemas.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{AUTO_CREATE_TARGET_SCHEMAS}}", request.AutoCreateTargetSchemas.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{VALIDATE_DEFINITIONS}}", request.EnableDataDefValidation.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{VALIDATE_DATA}}", request.EnableDataValidation.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{DATA_VALIDATION_ROW_LIMIT}}", request.DataValidationRowLimit.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{VALIDATE_FULL_DATASET}}", request.ValidateFullDataset.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{{DEGREE_OF_PARALLELISM}}", request.DegreeOfParallelism.ToString(), StringComparison.OrdinalIgnoreCase);

            var outMaster = Path.Combine(runDir, "master_generated.sql");
            File.WriteAllText(outMaster, rendered, Encoding.UTF8);

            var outReq = Path.Combine(runDir, "request_snapshot.json");
            File.WriteAllText(outReq, requestJson, Encoding.UTF8);

            appendLog($"[Template] Generated master script: {outMaster}");
            appendLog($"[Template] Saved request snapshot: {outReq}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Template] Failed to generate master artifacts: {ex.Message}");
        }
    }

private async Task<List<(string Schema, string Table)>> DiscoverTablesAsync(SqlConnection openSql, string dbName, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var query = _queries.Format("ListSqlTables", new Dictionary<string, string> { ["db"] = db });

        await using var cmd = new SqlCommand(query, openSql);
        var list = new List<(string Schema, string Table)>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            list.Add((rdr.GetString(0), rdr.GetString(1)));
        }
        return list;
    }

private async Task DeployTableAsync(SqlConnection openSql, OracleConnection openOra, string dbName, string schema, string table, string targetSchema, CancellationToken cancellationToken)
    {
        var columns = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, cancellationToken);
        var ddl = OracleDdlGenerator.CreateTableDdl(targetSchema, table, columns, _typeMapper);

        // IMPORTANT: Do not quote normal usernames/schemas (e.g., SYSTEM). Quoted usernames become case-sensitive and often fail.
        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var drop = $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {schemaPrefix}.{OracleIdent.QuoteIdent(table)}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        await using (var dropCmd = new OracleCommand(drop, openOra))
        {
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var cmd = new OracleCommand(ddl, openOra);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.Info($"Deployed table DDL: {schema}.{table}");

        // Primary keys, unique constraints and indexes must be migrated as well (core requirement).
        await DeployConstraintsAndIndexesAsync(openSql, openOra, dbName, schema, table, targetSchema, cancellationToken);
    }

private async Task CopyTableAsync(SqlConnection openSql, OracleConnection openOra, string dbName, string schema, string table, string targetSchema, CancellationToken cancellationToken)
    {

// FIX: DataMigration runs with DOP>1. DbConnection instances are not safe for concurrent readers/transactions.
// Prefer per-table connections. If providers sanitize ConnectionString after Open() (default Persist Security Info=false),
// passwords may be omitted and child connection Open() can fail (e.g., ORA-01005 / ORA-01017 / SQL Login failed).
// In that case, fall back to the already-open parent connection and serialize access via gates.

SqlConnection? sqlPerTable = null;
OracleConnection? oraPerTable = null;
var useSharedSql = false;
var useSharedOra = false;
var acquiredSqlGate = false;
var acquiredOraGate = false;

try
{
    // --- SQL child connection ---
    try
    {
        sqlPerTable = SqlChildConnectionFactory.CreateChildSqlConnection(openSql);
        await sqlPerTable.OpenAsync(cancellationToken);
        try { sqlPerTable.ChangeDatabase(dbName); } catch { /* best effort */ }
        openSql = sqlPerTable;
    }
    catch (SqlException ex) when (ex.Number == 18456 || ex.Message.IndexOf("Login failed", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        useSharedSql = true;
        if (sqlPerTable != null)
        {
            try { await sqlPerTable.DisposeAsync(); } catch { }
            sqlPerTable = null;
        }
        // keep openSql as the original parent connection (already open)
    }

    // --- Oracle child connection ---
    try
    {
        oraPerTable = OracleChildConnectionFactory.CreateChildOracleConnection(openOra);
        await oraPerTable.OpenAsync(cancellationToken);
        openOra = oraPerTable;
    }
    catch (OracleException ex) when (
        ex.Number == 1005 || // ORA-01005: null password
        ex.Number == 1017 || // ORA-01017: invalid username/password
        ex.Number == 50000 || // ORA-50000: connection request timed out (pool/driver)
        ex.Message.IndexOf("ORA-01005", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("ORA-01017", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("ORA-50000", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("Connection request timed out", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        useSharedOra = true;
        if (oraPerTable != null)
        {
            try { await oraPerTable.DisposeAsync(); } catch { }
            oraPerTable = null;
        }
        // keep openOra as the original parent connection (already open)
    }

    // Acquire gates only when we must use shared parent connections
    if (useSharedSql)
    {
        await _sharedSqlGate.WaitAsync(cancellationToken);
        acquiredSqlGate = true;
    }
    if (useSharedOra)
    {
        await _sharedOraGate.WaitAsync(cancellationToken);
        acquiredOraGate = true;
    }

var columns = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, cancellationToken);
        if (columns.Count == 0) return;

        var colNames = columns.OrderBy(c => c.Ordinal).Select(c => c.ColumnName).ToList();

        var db = SqlIdent.Bracket(dbName);
        var selectSql = _queries.Format("SqlSelectTableAll", new Dictionary<string, string> { ["db"] = db });

        await using var selectCmd = new SqlCommand(selectSql, openSql);
        selectCmd.Parameters.AddWithValue("@SchemaName", schema);
        selectCmd.Parameters.AddWithValue("@TableName", table);
        selectCmd.CommandTimeout = 0;

        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var insertSql = $"INSERT INTO {schemaPrefix}.{OracleIdent.QuoteIdent(table)} ({string.Join(",", colNames.Select(OracleIdent.QuoteIdent))}) VALUES ({string.Join(",", colNames.Select((c, i) => $":p{i}"))})";

        await using var txn = openOra.BeginTransaction();
        await using var insertCmd = new OracleCommand(insertSql, openOra)
        {
            Transaction = txn,
            BindByName = false
        };

insertCmd.Parameters.Clear();
// Ensure OracleParameter types and sizes align with target Oracle column metadata.
// Prevents ORA-00932 / ORA-01465 and also ORA-50028 (RAW binding requires size even for NULL values).
var targetColumnMeta = await GetOracleTargetColumnMetadataAsync(openOra, targetSchema, table, cancellationToken);

for (var i = 0; i < colNames.Count; i++)
{
    var col = colNames[i];
    var meta = targetColumnMeta.TryGetValue(col, out var m)
        ? m
        : new OracleColumnMeta(OracleDbType.Varchar2, null, null, null, true);

    var p = new OracleParameter($"p{i}", meta.DbType) { Value = DBNull.Value };

    // Preserve Oracle NOT NULL vs NULLABLE semantics for special cases (e.g., empty string handling).
    p.IsNullable = meta.IsNullable;

    if (meta.Size.HasValue && meta.Size.Value > 0)
        p.Size = meta.Size.Value;

    if (meta.Precision.HasValue && meta.Precision.Value > 0)
        p.Precision = meta.Precision.Value;

// Ensure RAW/LongRaw parameters always have a binding size (ODP.NET may throw ORA-50028 even when value is NULL).
const int defaultRawSize = 2000;
if ((meta.DbType == OracleDbType.Raw || meta.DbType == OracleDbType.LongRaw) && (p.Size <= 0))
    p.Size = defaultRawSize;

        if (meta.Scale.HasValue)
        p.Scale = meta.Scale.Value;

    insertCmd.Parameters.Add(p);
}

const int batchCommit = 2000;
        var pending = 0;

        const int maxPreviewChars = 256;
        long rowNumber = 0;

        await using var rdr = await selectCmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;

            var batchNumber = (int)((rowNumber - 1) / batchCommit) + 1;
            var batchRowIndex = (int)((rowNumber - 1) % batchCommit) + 1;

            for (var i = 0; i < colNames.Count; i++)
            {
                object? rawValue = rdr.IsDBNull(i) ? null : rdr.GetValue(i);

                TryAssignOracleParameterValue(
                    stage: "DataMigration",
                    schema: schema,
                    table: table,
                    rowNumber: rowNumber,
                    batchNumber: batchNumber,
                    batchRowIndex: batchRowIndex,
                    sourceColumn: colNames[i],
                    targetColumn: colNames[i],
                    param: insertCmd.Parameters[i],
                    rawValue: rawValue,
                    maxPreviewChars: maxPreviewChars);
            }

            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            pending++;

            if (pending >= batchCommit)
            {
                await insertCmd.Transaction!.CommitAsync(cancellationToken);
                pending = 0;

                // Begin new transaction before disposing the old one to maintain consistent state
                var oldTxn = insertCmd.Transaction;
                insertCmd.Transaction = openOra.BeginTransaction();
                oldTxn?.Dispose();
            }
        }

        if (pending > 0)
            await insertCmd.Transaction!.CommitAsync(cancellationToken);
    

}
finally
{
    if (acquiredOraGate) _sharedOraGate.Release();
    if (acquiredSqlGate) _sharedSqlGate.Release();

    if (oraPerTable != null)
    {
        try { await oraPerTable.DisposeAsync(); } catch { }
    }
    if (sqlPerTable != null)
    {
        try { await sqlPerTable.DisposeAsync(); } catch { }
    }
}
    }

private async Task ValidateTableDataAsync(
        SqlConnection openSql,
        OracleConnection openOra,
        string dbName,
        string schema,
        string table,
        string targetSchema,
        bool validateFullDataset,
        int rowLimit,
        CancellationToken cancellationToken)
    {
        var columns = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, cancellationToken);
        if (columns.Count == 0) return;

        var colNames = columns.OrderBy(c => c.Ordinal).Select(c => c.ColumnName).ToList();

        var db = SqlIdent.Bracket(dbName);

        string selectSql;
        if (validateFullDataset)
        {
            selectSql = _queries.Format("SqlSelectTableAll", new Dictionary<string, string> { ["db"] = db });
        }
        else
        {
            selectSql = _queries.Format("SqlSelectTableTopN", new Dictionary<string, string> { ["db"] = db });
        }

        await using var selectCmd = new SqlCommand(selectSql, openSql);
        selectCmd.Parameters.AddWithValue("@SchemaName", schema);
        selectCmd.Parameters.AddWithValue("@TableName", table);
        if (!validateFullDataset)
            selectCmd.Parameters.AddWithValue("@TopN", Math.Max(1, rowLimit));

        selectCmd.CommandTimeout = 0;

        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var insertSql = $"INSERT INTO {schemaPrefix}.{OracleIdent.QuoteIdent(table)} ({string.Join(",", colNames.Select(OracleIdent.QuoteIdent))}) VALUES ({string.Join(",", colNames.Select((c, i) => $":p{i}"))})";

        await using var txn = openOra.BeginTransaction();
        await using var insertCmd = new OracleCommand(insertSql, openOra)
        {
            Transaction = txn,
            BindByName = false
        };

insertCmd.Parameters.Clear();
// Ensure OracleParameter types and sizes align with target Oracle column metadata.
// Prevents ORA-00932 / ORA-01465 and also ORA-50028 (RAW binding requires size even for NULL values).
var targetColumnMeta = await GetOracleTargetColumnMetadataAsync(openOra, targetSchema, table, cancellationToken);

for (var i = 0; i < colNames.Count; i++)
{
    var col = colNames[i];
    var meta = targetColumnMeta.TryGetValue(col, out var m)
        ? m
        : new OracleColumnMeta(OracleDbType.Varchar2, null, null, null, true);

    var p = new OracleParameter($"p{i}", meta.DbType) { Value = DBNull.Value };

    // Preserve Oracle NOT NULL vs NULLABLE semantics for special cases (e.g., empty string handling).
    p.IsNullable = meta.IsNullable;

    if (meta.Size.HasValue && meta.Size.Value > 0)
        p.Size = meta.Size.Value;

    if (meta.Precision.HasValue && meta.Precision.Value > 0)
        p.Precision = meta.Precision.Value;

// Ensure RAW/LongRaw parameters always have a binding size (ODP.NET may throw ORA-50028 even when value is NULL).
const int defaultRawSize = 2000;
if ((meta.DbType == OracleDbType.Raw || meta.DbType == OracleDbType.LongRaw) && (p.Size <= 0))
    p.Size = defaultRawSize;

        if (meta.Scale.HasValue)
        p.Scale = meta.Scale.Value;

    insertCmd.Parameters.Add(p);
}

const int maxPreviewChars = 256;
        var written = 0;
        long rowNumber = 0;
        await using var rdr = await selectCmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;

            // DataValidation does not commit; keep batch fields deterministic for diagnostics.
            var batchNumber = 0;
            var batchRowIndex = rowNumber > int.MaxValue ? int.MaxValue : (int)rowNumber;

            for (var i = 0; i < colNames.Count; i++)
            {
                object? rawValue = rdr.IsDBNull(i) ? null : rdr.GetValue(i);

                TryAssignOracleParameterValue(
                    stage: "DataValidation",
                    schema: schema,
                    table: table,
                    rowNumber: rowNumber,
                    batchNumber: batchNumber,
                    batchRowIndex: batchRowIndex,
                    sourceColumn: colNames[i],
                    targetColumn: colNames[i],
                    param: insertCmd.Parameters[i],
                    rawValue: rawValue,
                    maxPreviewChars: maxPreviewChars);
            }

try
{
    await insertCmd.ExecuteNonQueryAsync(cancellationToken);
}
catch (ArgumentException aex)
{
    // ORA-50028 is surfaced by ODP.NET as ArgumentException during pre-bind. Dump param metadata for root-cause.
    var paramDump = string.Join(", ",
        insertCmd.Parameters.Cast<OracleParameter>()
            .Select(p => $"{p.ParameterName}:{p.OracleDbType}(Size={p.Size},Prec={p.Precision},Scale={p.Scale})={SafeValuePreview(p.Value, maxPreviewChars)}"));

    throw new MigrationDataBindingException(
        stage: "DataValidation",
        schema: schema,
        objectName: table,
        rowNumber: rowNumber,
        batchNumber: batchNumber,
        batchRowIndex: batchRowIndex,
        sourceColumn: "<ROW>",
        targetColumn: "<ROW>",
        oracleParameterName: "<ROW>",
        oracleDbType: "<ROW>",
        size: null,
        precision: null,
        scale: null,
        valueType: "ArgumentException",
        valuePreview: $"{aex.Message} | Params: {paramDump}",
        inner: aex);
}
catch (OracleException oex)
{
    // Provide row-level diagnostics (param names/types/value previews) to speed up remediation.
    var paramDump = string.Join(", ",
        insertCmd.Parameters.Cast<OracleParameter>()
            .Select(p => $"{p.ParameterName}:{p.OracleDbType}={SafeValuePreview(p.Value, maxPreviewChars)}"));

    throw new MigrationDataBindingException(
        stage: "DataValidation",
        schema: schema,
        objectName: table,
        rowNumber: rowNumber,
        batchNumber: batchNumber,
        batchRowIndex: batchRowIndex,
        sourceColumn: "<ROW>",
        targetColumn: "<ROW>",
        oracleParameterName: "<ROW>",
        oracleDbType: "<ROW>",
        size: null,
        precision: null,
        scale: null,
        valueType: "OracleException",
        valuePreview: $"ORA-{oex.Number}: {oex.Message} | Params: {paramDump}",
        inner: oex);
}
            written++;

            if (!validateFullDataset && written >= rowLimit)
                break;
        }

        // Rollback always (dry-run)
        try
        {
            await txn.RollbackAsync(cancellationToken);
        }
        catch
        {
            // ignore rollback exceptions (best-effort)
        }

        _logger.Info($"[DataValidation] Dry-run inserted {written} row(s) for {schema}.{table} into {schemaPrefix}.{table} (rolled back).");
    }

private static async Task<long> GetOracleTableRowCountAsync(OracleConnection openOra, string schema, string table, CancellationToken cancellationToken)
    {
        var schemaPrefix = OracleIdent.FormatSchema(schema);
        var sql = $"SELECT COUNT(*) FROM {schemaPrefix}.{OracleIdent.QuoteIdent(table)}";
        await using var cmd = new OracleCommand(sql, openOra);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

private void WriteStageReport(string runDir, string stageName, IReadOnlyList<StageError> issues)
    {
        try
        {
            var fileTxt = Path.Combine(runDir, $"{stageName}_errors.txt");
            var fileJson = Path.Combine(runDir, $"{stageName}_errors.json");

            if (issues.Count == 0)
            {
                File.WriteAllText(fileTxt, $"{stageName}: No issues detected.{Environment.NewLine}");
                File.WriteAllText(fileJson, "[]");
                return;
            }

            var lines = new List<string>
            {
                $"{stageName}: {issues.Count} error(s) detected.",
                "------------------------------------------------------------"
            };
            lines.AddRange(issues.Select(e => $"{e.Schema}.{e.Object}: {e.ErrorType}: {e.Message}"));
            File.WriteAllLines(fileTxt, lines);
            File.WriteAllText(fileJson, JsonSerializer.Serialize(issues, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to write {stageName} report: {ex.Message}");
        }
    }

private static string SafeValuePreview(object? value, int maxChars)
    {
        if (value == null || value is DBNull) return "<NULL>";

        // Avoid dumping large/binary values
        if (value is byte[] bytes)
            return $"<byte[{bytes.Length}] sha256={Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 16)}>";

        if (value is OracleBlob oblob)
            return $"<OracleBlob len={oblob.Length}>";

        if (value is OracleClob oclob)
            return $"<OracleClob len={oclob.Length}>";

        var s = value.ToString() ?? "";
        if (s.Length <= maxChars) return s;
        return s.Substring(0, maxChars) + $"â€¦(truncated, len={s.Length})";
    }


private sealed record OracleColumnMeta(OracleDbType DbType, int? Size, byte? Precision, byte? Scale, bool IsNullable);

private static async Task<Dictionary<string, OracleColumnMeta>> GetOracleTargetColumnMetadataAsync(
    OracleConnection conn,
    string targetSchema,
    string targetTable,
    CancellationToken ct)
{
    var ownerRaw = (targetSchema ?? string.Empty).Trim().Trim('"');
    var tableRaw = (targetTable ?? string.Empty).Trim().Trim('"');

    // If quoted identifiers were created ( e.g., "Employee"), ALL_TAB_COLUMNS stores them case-sensitively.
    // So we try both raw-case and upper-case variants.
    var ownerUpper = ownerRaw.ToUpperInvariant();
    var tableUpper = tableRaw.ToUpperInvariant();

    using var cmd = conn.CreateCommand();
    cmd.BindByName = true;
    cmd.CommandText = @"
        SELECT column_name,
               data_type,
               data_length,
               char_length,
               data_precision,
               data_scale,
               nullable
        FROM all_tab_columns
        WHERE (owner = :p_owner_raw OR owner = :p_owner_upper)
          AND (table_name = :p_table_raw OR table_name = :p_table_upper)
        ORDER BY column_id";
    cmd.Parameters.Add(new OracleParameter("p_owner_raw", OracleDbType.Varchar2, ownerRaw, System.Data.ParameterDirection.Input));
    cmd.Parameters.Add(new OracleParameter("p_owner_upper", OracleDbType.Varchar2, ownerUpper, System.Data.ParameterDirection.Input));
    cmd.Parameters.Add(new OracleParameter("p_table_raw", OracleDbType.Varchar2, tableRaw, System.Data.ParameterDirection.Input));
    cmd.Parameters.Add(new OracleParameter("p_table_upper", OracleDbType.Varchar2, tableUpper, System.Data.ParameterDirection.Input));

    var map = new Dictionary<string, OracleColumnMeta>(StringComparer.OrdinalIgnoreCase);
    await using var rdr = await cmd.ExecuteReaderAsync(ct);
    while (await rdr.ReadAsync(ct))
    {
        var col = rdr.GetString(0);
        var dt = rdr.GetString(1);

        int? dataLen = rdr.IsDBNull(2) ? null : Convert.ToInt32(rdr.GetDecimal(2));
        int? charLen = rdr.IsDBNull(3) ? null : Convert.ToInt32(rdr.GetDecimal(3));
        byte? precision = rdr.IsDBNull(4) ? null : (byte?)Convert.ToInt32(rdr.GetDecimal(4));
        byte? scale = rdr.IsDBNull(5) ? null : (byte?)Convert.ToInt32(rdr.GetDecimal(5));

        

        var nullableFlag = rdr.IsDBNull(6) ? "Y" : rdr.GetString(6);
        var isNullable = string.Equals(nullableFlag, "Y", StringComparison.OrdinalIgnoreCase);
var dbType = MapOracleDataType(dt);

        int? size = null;
        switch (dbType)
        {
            case OracleDbType.Varchar2:
            case OracleDbType.NVarchar2:
            case OracleDbType.Char:
            case OracleDbType.NChar:
                size = charLen ?? dataLen;
                break;

            case OracleDbType.Raw:
            case OracleDbType.LongRaw:
                size = dataLen;
                break;
        }

        map[col] = new OracleColumnMeta(dbType, size, precision, scale, isNullable);
    }

    return map;
}


private static OracleDbType MapOracleDataType(string dataType)
{
    if (string.IsNullOrWhiteSpace(dataType)) return OracleDbType.Varchar2;
    var dt = dataType.Trim().ToUpperInvariant();

    // Handle TIMESTAMP variants reliably (Oracle may return different exact strings depending on view).
    if (dt.StartsWith("TIMESTAMP", StringComparison.Ordinal))
    {
        if (dt.Contains("WITH LOCAL TIME ZONE", StringComparison.Ordinal))
            return OracleDbType.TimeStampLTZ;
        if (dt.Contains("WITH TIME ZONE", StringComparison.Ordinal))
            return OracleDbType.TimeStampTZ;
        return OracleDbType.TimeStamp;
    }

// Handle INTERVAL types (commonly used when mapping SQL Server TIME to Oracle INTERVAL DAY TO SECOND).
if (dt.StartsWith("INTERVAL DAY", StringComparison.Ordinal)) return OracleDbType.IntervalDS;
if (dt.StartsWith("INTERVAL YEAR", StringComparison.Ordinal)) return OracleDbType.IntervalYM;

    if (dt.StartsWith("VARCHAR2", StringComparison.Ordinal)) return OracleDbType.Varchar2;
    if (dt.StartsWith("NVARCHAR2", StringComparison.Ordinal)) return OracleDbType.NVarchar2;
    if (dt.StartsWith("CHAR", StringComparison.Ordinal)) return OracleDbType.Char;
    if (dt.StartsWith("NCHAR", StringComparison.Ordinal)) return OracleDbType.NChar;

    if (dt.StartsWith("NUMBER", StringComparison.Ordinal)) return OracleDbType.Decimal;
    if (dt.StartsWith("FLOAT", StringComparison.Ordinal)) return OracleDbType.Single;
    if (dt.StartsWith("BINARY_FLOAT", StringComparison.Ordinal)) return OracleDbType.BinaryFloat;
    if (dt.StartsWith("BINARY_DOUBLE", StringComparison.Ordinal)) return OracleDbType.BinaryDouble;

    if (dt.StartsWith("DATE", StringComparison.Ordinal)) return OracleDbType.Date;

    if (dt.StartsWith("CLOB", StringComparison.Ordinal)) return OracleDbType.Clob;
    if (dt.StartsWith("NCLOB", StringComparison.Ordinal)) return OracleDbType.NClob;
    if (dt.StartsWith("BLOB", StringComparison.Ordinal)) return OracleDbType.Blob;

    if (dt.StartsWith("LONG RAW", StringComparison.Ordinal)) return OracleDbType.LongRaw;
    if (dt.StartsWith("RAW", StringComparison.Ordinal)) return OracleDbType.Raw;
    if (dt.StartsWith("LONG", StringComparison.Ordinal)) return OracleDbType.Long;

    return OracleDbType.Varchar2;
}

private static object? NormalizeOracleParameterValue(OracleParameter param, object? rawValue)
{

// Oracle treats empty strings as NULL for VARCHAR2/NVARCHAR2/CHAR/NCHAR.
// For NOT NULL columns, binding an empty string would violate the constraint (ORA-01400).
// Policy: if target column is NOT NULL, store a single space to preserve "non-null" intent.
if (rawValue is string s1 && s1.Length == 0)
{
    if (param.OracleDbType is OracleDbType.Varchar2 or OracleDbType.NVarchar2 or OracleDbType.Char or OracleDbType.NChar)
    {
        return param.IsNullable ? DBNull.Value : " ";
    }
}

// Handle common SQL CLR / SqlTypes values returned by SqlDataReader
// without taking a hard dependency on Microsoft.SqlServer.Types in this project.
if (rawValue is System.Data.SqlTypes.SqlBinary sb)
    return sb.Value;

if (rawValue is System.Data.SqlTypes.SqlBytes sby)
    return sby.Value;

if (rawValue is System.Data.SqlTypes.SqlGuid sg)
    rawValue = sg.Value;

// SQL Server CLR types (hierarchyid / geography / geometry) - use reflection so this compiles even if the assembly isn't referenced.
var tFull = rawValue?.GetType().FullName;
if (tFull == "Microsoft.SqlServer.Types.SqlHierarchyId")
{
    // Prefer binary representation for RAW/BLOB targets
    if (param.OracleDbType is OracleDbType.Raw or OracleDbType.LongRaw or OracleDbType.Blob)
    {
        var getBinary = rawValue!.GetType().GetMethod("GetBinary", Type.EmptyTypes);
        var sqlBytes = getBinary?.Invoke(rawValue, null);
        var valProp = sqlBytes?.GetType().GetProperty("Value");
        var bytes = valProp?.GetValue(sqlBytes) as byte[];
        return bytes ?? Array.Empty<byte>();
    }
    return rawValue!.ToString();
}

if (tFull == "Microsoft.SqlServer.Types.SqlGeography" || tFull == "Microsoft.SqlServer.Types.SqlGeometry")
{
    if (param.OracleDbType is OracleDbType.Raw or OracleDbType.LongRaw or OracleDbType.Blob)
    {
        var stAsBinary = rawValue!.GetType().GetMethod("STAsBinary", Type.EmptyTypes);
        var sqlBytes = stAsBinary?.Invoke(rawValue, null);
        var valProp = sqlBytes?.GetType().GetProperty("Value");
        var bytes = valProp?.GetValue(sqlBytes) as byte[];
        return bytes ?? Array.Empty<byte>();
    }
    return rawValue!.ToString();
}

    if (rawValue == null || rawValue is DBNull) return null;

    // Fix: SQL Server BIT is read as System.Boolean. Binding bool directly can make ODP.NET treat it as BOOLEAN,
    // which causes ORA-00932 during INSERT (Oracle SQL expects CHAR/NUMBER, not BOOLEAN).
    if (rawValue is bool b)
    {
        // If the parameter is (or will be) character, bind as "1"/"0"; otherwise bind as numeric 1/0.
        switch (param.OracleDbType)
        {
            case OracleDbType.Char:
            case OracleDbType.NChar:
            case OracleDbType.Varchar2:
            case OracleDbType.NVarchar2:
                return b ? "1" : "0";
            default:
                return (short)(b ? 1 : 0);
        }
    }

    // Fix: SQL Server UNIQUEIDENTIFIER is read as System.Guid. ODP.NET does not accept Guid for Varchar2/Char parameters.
    if (rawValue is Guid g)
    {
        switch (param.OracleDbType)
        {
            case OracleDbType.Varchar2:
            case OracleDbType.NVarchar2:
            case OracleDbType.Char:
            case OracleDbType.NChar:
                return g.ToString(); // 36-char canonical string

            case OracleDbType.Raw:
            case OracleDbType.LongRaw:
                return g.ToByteArray(); // 16 bytes

            default:
                return rawValue;
        }
    }

    
// Fix: RAW columns may receive string values (hex literals or GUID text). Convert to byte[].
if (param.OracleDbType is OracleDbType.Raw or OracleDbType.LongRaw)
{
    if (rawValue is string s2)
    {
        if (Guid.TryParse(s2, out var sg2))
            return sg2.ToByteArray();

        var hex = s2.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex.Substring(2);

        // remove common separators
        hex = hex.Replace("-", "").Replace(" ", "");

        // validate hex
        if (hex.Length % 2 != 0)
            throw new ArgumentException($"RAW hex string has odd length: {hex.Length}");

        for (int i = 0; i < hex.Length; i++)
        {
            char c = hex[i];
            bool ok = (c >= '0' && c <= '9') ||
                      (c >= 'a' && c <= 'f') ||
                      (c >= 'A' && c <= 'F');
            if (!ok) throw new ArgumentException($"RAW hex string contains non-hex character '{c}'");
        }

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

        return bytes;
    }
}

// Normalize SQL Server TIME (TimeSpan) for Oracle INTERVAL DAY TO SECOND columns.
// SQL Server 'time' usually arrives as TimeSpan; binding as IntervalDS avoids ORA-01867 (invalid interval) from implicit CHAR->INTERVAL conversion.
if (param.OracleDbType == OracleDbType.IntervalDS)
{
    if (rawValue is TimeSpan ts)
        return ts;

    if (rawValue is DateTime dt)
        return dt.TimeOfDay;

    if (rawValue is string s3 && TimeSpan.TryParse(s3, out var tsParsed))
        return tsParsed;

    // Let other types fall through; ODP may throw a binding exception with diagnostics.
}
// Normalize DateTimeOffset unless mapping supports TZ types
    if (rawValue is DateTimeOffset dto)
    {
        switch (param.OracleDbType)
        {
            case OracleDbType.TimeStampTZ:
            case OracleDbType.TimeStampLTZ:
                return dto;
            default:
                return dto.UtcDateTime;
        }
    }

    return rawValue;
}

private void TryAssignOracleParameterValue(
    string stage,
    string schema,
    string table,
    long rowNumber,
    int batchNumber,
    int batchRowIndex,
    string sourceColumn,
    string targetColumn,
    OracleParameter param,
    object? rawValue,
    int maxPreviewChars)
{
    // Fallback: If a SQL datetime/datetime2 value arrives, but the OracleParameter was created as a character type
// (e.g., due to metadata lookup gaps during resume), Oracle may attempt implicit CHAR->DATE conversion and raise ORA-01843.
// Binding it as Date avoids the parse path and works for both DATE and VARCHAR2 target columns.
if (rawValue is DateTime && (param.OracleDbType is OracleDbType.Varchar2 or OracleDbType.NVarchar2 or OracleDbType.Char or OracleDbType.NChar))
{
    param.OracleDbType = OracleDbType.Date;
}

try
    {
        // Keep existing behavior: DBNull for nulls
        param.Value = (rawValue == null || rawValue is DBNull)
            ? DBNull.Value
            : NormalizeOracleParameterValue(param, rawValue)!;
// ODP.NET can throw ORA-50028 for RAW parameters when Value is NULL and Size is not known.
// Ensure Size is set for RAW/LongRaw based on the actual value when available.
if (param.OracleDbType is OracleDbType.Raw or OracleDbType.LongRaw)
{
    if (param.Value is byte[] b && b.Length > 0 && param.Size <= 0)
        param.Size = b.Length;
}

    }
    catch (ArgumentException ex)
    {
        var preview = SafeValuePreview(rawValue, maxPreviewChars);
        var valueType = rawValue == null ? "<NULL>" : rawValue.GetType().FullName ?? rawValue.GetType().Name;

        throw new MigrationDataBindingException(
            stage: stage,
            schema: schema,
            objectName: table,
            rowNumber: rowNumber,
            batchNumber: batchNumber,
            batchRowIndex: batchRowIndex,
            sourceColumn: sourceColumn,
            targetColumn: targetColumn,
            oracleParameterName: param.ParameterName ?? "",
            oracleDbType: param.OracleDbType.ToString(),
            size: param.Size == 0 ? null : param.Size,
            precision: param.Precision == 0 ? null : (byte?)param.Precision,
            scale: param.Scale == 0 ? null : (byte?)param.Scale,
            valueType: valueType,
            valuePreview: preview,
            inner: ex);
    }
}

private void WriteRunSummary(string runDir, MigrationRunSummary summary)
    {
        try
        {
            var file = Path.Combine(runDir, "run_summary.json");
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file, json);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to write run summary: {ex.Message}");
        }
    }

private void Raise(MigrationStage stage, string message, double? pct = null)
    {
        _logger.Info($"[{stage}] {message}");
        Progress?.Invoke(this, new MigrationProgress(stage, message, pct));
    }







private static string BuildDropAllObjectsPlSql_UserObjects() => @"
DECLARE
  PROCEDURE safe_exec(p_sql VARCHAR2) IS
  BEGIN
    EXECUTE IMMEDIATE p_sql;
  EXCEPTION WHEN OTHERS THEN NULL;
  END;
BEGIN
  FOR r IN (SELECT object_name FROM user_objects WHERE object_type = 'MATERIALIZED VIEW') LOOP
    safe_exec('DROP MATERIALIZED VIEW ""' || r.object_name || '""');
  END LOOP;

  FOR r IN (SELECT object_name FROM user_objects WHERE object_type = 'VIEW') LOOP
    safe_exec('DROP VIEW ""' || r.object_name || '""');
  END LOOP;

  FOR r IN (SELECT object_name FROM user_objects WHERE object_type = 'SYNONYM') LOOP
    safe_exec('DROP SYNONYM ""' || r.object_name || '""');
  END LOOP;

  FOR r IN (SELECT object_name FROM user_objects WHERE object_type = 'SEQUENCE') LOOP
    safe_exec('DROP SEQUENCE ""' || r.object_name || '""');
  END LOOP;

  FOR r IN (SELECT object_name FROM user_objects WHERE object_type = 'TABLE') LOOP
    safe_exec('DROP TABLE ""' || r.object_name || '"" CASCADE CONSTRAINTS PURGE');
  END LOOP;

  FOR r IN (SELECT object_name FROM user_objects WHERE object_type = 'INDEX') LOOP
    safe_exec('DROP INDEX ""' || r.object_name || '""');
  END LOOP;

  FOR r IN (SELECT object_name FROM user_objects WHERE object_type = 'TYPE') LOOP
    safe_exec('DROP TYPE ""' || r.object_name || '"" FORCE');
  END LOOP;

  FOR r IN (SELECT object_name FROM user_objects WHERE object_type = 'PACKAGE') LOOP
    safe_exec('DROP PACKAGE ""' || r.object_name || '""');
  END LOOP;

  FOR r IN (SELECT object_name, object_type FROM user_objects WHERE object_type IN ('PROCEDURE','FUNCTION')) LOOP
    safe_exec('DROP ' || r.object_type || ' ""' || r.object_name || '""');
  END LOOP;

  FOR r IN (SELECT object_name FROM user_objects WHERE object_type = 'TRIGGER') LOOP
    safe_exec('DROP TRIGGER ""' || r.object_name || '""');
  END LOOP;

  safe_exec('PURGE RECYCLEBIN');
END;";

    private static string BuildDropAllObjectsPlSql_AllObjectsOwnerQualified() => @"
DECLARE
  v_owner VARCHAR2(128) := :p_owner;

  PROCEDURE safe_exec(p_sql VARCHAR2) IS
  BEGIN
    EXECUTE IMMEDIATE p_sql;
  EXCEPTION WHEN OTHERS THEN NULL;
  END;

  FUNCTION qname(p_obj VARCHAR2) RETURN VARCHAR2 IS
  BEGIN
    RETURN '""' || v_owner || '"".""' || p_obj || '""';
  END;
BEGIN
  FOR r IN (SELECT object_name FROM all_objects WHERE owner = v_owner AND object_type = 'MATERIALIZED VIEW') LOOP
    safe_exec('DROP MATERIALIZED VIEW ' || qname(r.object_name));
  END LOOP;

  FOR r IN (SELECT object_name FROM all_objects WHERE owner = v_owner AND object_type = 'VIEW') LOOP
    safe_exec('DROP VIEW ' || qname(r.object_name));
  END LOOP;

  FOR r IN (SELECT object_name FROM all_objects WHERE owner = v_owner AND object_type = 'SYNONYM') LOOP
    safe_exec('DROP SYNONYM ' || qname(r.object_name));
  END LOOP;

  FOR r IN (SELECT object_name FROM all_objects WHERE owner = v_owner AND object_type = 'SEQUENCE') LOOP
    safe_exec('DROP SEQUENCE ' || qname(r.object_name));
  END LOOP;

  FOR r IN (SELECT object_name FROM all_objects WHERE owner = v_owner AND object_type = 'TABLE') LOOP
    safe_exec('DROP TABLE ' || qname(r.object_name) || ' CASCADE CONSTRAINTS PURGE');
  END LOOP;

  FOR r IN (SELECT object_name FROM all_objects WHERE owner = v_owner AND object_type = 'INDEX') LOOP
    safe_exec('DROP INDEX ' || qname(r.object_name));
  END LOOP;

  FOR r IN (SELECT object_name FROM all_objects WHERE owner = v_owner AND object_type = 'TYPE') LOOP
    safe_exec('DROP TYPE ' || qname(r.object_name) || ' FORCE');
  END LOOP;

  FOR r IN (SELECT object_name FROM all_objects WHERE owner = v_owner AND object_type = 'PACKAGE') LOOP
    safe_exec('DROP PACKAGE ' || qname(r.object_name));
  END LOOP;

  FOR r IN (SELECT object_name, object_type FROM all_objects WHERE owner = v_owner AND object_type IN ('PROCEDURE','FUNCTION')) LOOP
    safe_exec('DROP ' || r.object_type || ' ' || qname(r.object_name));
  END LOOP;

  FOR r IN (SELECT object_name FROM all_objects WHERE owner = v_owner AND object_type = 'TRIGGER') LOOP
    safe_exec('DROP TRIGGER ' || qname(r.object_name));
  END LOOP;

  safe_exec('PURGE RECYCLEBIN');
END;";
}

internal static class SqlChildConnectionFactory
{
    internal static SqlConnection CreateChildSqlConnection(SqlConnection parent)
    {
        if (parent == null) throw new ArgumentNullException(nameof(parent));

        try
        {
            var cred = parent.Credential;
            if (cred != null)
            {
                var ctor = typeof(SqlConnection).GetConstructor(new[] { typeof(string), typeof(SqlCredential) });
                if (ctor != null)
	                    return (SqlConnection)ctor.Invoke(new object[] { parent.ConnectionString, cred });
            }
        }
        catch
        {
            // ignore
        }

	        // NOTE: if Persist Security Info=false (default), SqlConnection.ConnectionString can be *sanitized*
	        // after Open() and may not include the password. That breaks per-table child connections when using SQL auth.
	        // We attempt to recover the original (unsanitized) connection string from non-public state.
	        var connString = TryGetRawConnectionString(parent) ?? parent.ConnectionString;
	        var child = new SqlConnection(connString);

        try
        {
            if (!string.IsNullOrWhiteSpace(parent.AccessToken))
                child.AccessToken = parent.AccessToken;
        }
        catch
        {
            // ignore
        }

        return child;
    }

	    private static string? TryGetRawConnectionString(SqlConnection parent)
	    {
	        try
	        {
	            // Fast path: if the public ConnectionString already contains a password, use it.
	            var publicCs = parent.ConnectionString;
	            if (!string.IsNullOrWhiteSpace(publicCs) && ContainsPasswordToken(publicCs))
	                return publicCs;

	            // Heuristic: scan private fields up the inheritance chain looking for a string that still contains password.
	            // This avoids hardcoding internal field names which can change between versions.
	            for (var t = parent.GetType(); t != null; t = t.BaseType)
	            {
	                var fields = t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
	                foreach (var f in fields)
	                {
	                    if (f.FieldType != typeof(string)) continue;
	                    if (f.GetValue(parent) is not string s) continue;
	                    if (string.IsNullOrWhiteSpace(s)) continue;
	                    if (ContainsPasswordToken(s)) return s;
	                }
	            }
	        }
	        catch
	        {
	            // ignore
	        }
	        return null;
	    }

	    private static bool ContainsPasswordToken(string cs)
	    {
	        // SQL Server aliases for password can be: Password / Pwd.
	        return cs.IndexOf("Password=", StringComparison.OrdinalIgnoreCase) >= 0
	            || cs.IndexOf("Pwd=", StringComparison.OrdinalIgnoreCase) >= 0;
	    }
}

	internal static class OracleChildConnectionFactory
	{
	    internal static OracleConnection CreateChildOracleConnection(OracleConnection parent)
	    {
	        if (parent == null) throw new ArgumentNullException(nameof(parent));

	        // OracleConnection.ConnectionString is usually preserved, but for safety we also try to recover
	        // an unsanitized form via reflection if needed.
	        var cs = TryGetRawOracleConnectionString(parent) ?? parent.ConnectionString;
	        return new OracleConnection(cs);
	    }

	    private static string? TryGetRawOracleConnectionString(OracleConnection parent)
	    {
	        try
	        {
	            var publicCs = parent.ConnectionString;
	            if (!string.IsNullOrWhiteSpace(publicCs) && ContainsPasswordToken(publicCs))
	                return publicCs;

	            for (var t = parent.GetType(); t != null; t = t.BaseType)
	            {
	                var fields = t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
	                foreach (var f in fields)
	                {
	                    if (f.FieldType != typeof(string)) continue;
	                    if (f.GetValue(parent) is not string s) continue;
	                    if (string.IsNullOrWhiteSpace(s)) continue;
	                    if (ContainsPasswordToken(s)) return s;
	                }
	            }
	        }
	        catch
	        {
	            // ignore
	        }
	        return null;
	    }

	    private static bool ContainsPasswordToken(string cs)
	    {
	        // Common Oracle keys: Password / Pwd.
	        return cs.IndexOf("Password=", StringComparison.OrdinalIgnoreCase) >= 0
	            || cs.IndexOf("Pwd=", StringComparison.OrdinalIgnoreCase) >= 0;
	    }
	}