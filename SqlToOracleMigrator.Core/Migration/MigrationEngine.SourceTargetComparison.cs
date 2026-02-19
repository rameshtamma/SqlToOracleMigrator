using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core.Tracking;
using System.Text;
using System.Text.Json;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Generates an end-user friendly comparison report (source vs target) to reduce manual DB querying.
    /// Called from Stage 10 (FinalVerification).
    /// </summary>
    internal async Task GenerateSourceTargetComparisonReportAsync(MigrationContext ctx, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var report = new SourceTargetComparisonReport
        {
            RunId = ctx.Summary.RunId,
            SourceDatabase = ctx.Request.SourceDatabase,
            TargetService = ctx.Request.TargetOracleConnection.ServiceName ?? ctx.Request.TargetOracleConnection.Sid ?? "",
            TargetPdb = ctx.Request.TargetPdbName,
            StartedUtc = started,
            Tables = new List<SourceTargetTableComparison>()
        };

        try
        {
            // Prefer discovered tables if available; otherwise infer from request mappings.
            var tables = (ctx.Tables is { Count: > 0 })
                ? ctx.Tables
                : new List<(string Schema, string Table)>();

            if (tables.Count == 0)
            {
                // Last resort: read SQL Server tables list for dbo.
                tables = await ListSqlServerTablesAsync(ctx.OpenSql, ct);
            }

            foreach (var (srcSchema, srcTable) in tables)
            {
                ct.ThrowIfCancellationRequested();

                var tgtSchema = ctx.GetTargetSchema(srcSchema);
                var tgtTable = srcTable;

                var item = new SourceTargetTableComparison
                {
                    SourceSchema = srcSchema,
                    SourceTable = srcTable,
                    TargetSchema = tgtSchema,
                    TargetTable = tgtTable
                };

                try
                {
                    item.SourceRowCount = await GetSqlRowCountAsync(ctx.OpenSql, ctx.Request.SourceDatabase, srcSchema, srcTable, ct);
                }
                catch (Exception ex)
                {
                    item.SourceRowCountError = ex.Message;
                }

                try
                {
                    var exists = await OracleTableExistsAsync(ctx.OpenOra, tgtSchema, tgtTable, ct);
                    item.TargetExists = exists;
                    if (exists)
                    {
                        item.TargetRowCount = await GetOracleRowCountAsync(ctx.OpenOra, tgtSchema, tgtTable, ctx.Request.UseUnquotedUppercaseIdentifiers, ct);
                    }
                    else
                    {
                        item.TargetRowCountError = "Target table not found";
                    }
                }
                catch (Exception ex)
                {
                    item.TargetRowCountError = ex.Message;
                }

                if (item.SourceRowCount.HasValue && item.TargetRowCount.HasValue)
                {
                    item.Delta = item.TargetRowCount.Value - item.SourceRowCount.Value;
                }

                report.Tables.Add(item);
            }

            report.CompletedUtc = DateTimeOffset.UtcNow;
            report.DurationSeconds = Math.Round((report.CompletedUtc.Value - started).TotalSeconds, 3);

            // Summary rollups
            report.TotalTables = report.Tables.Count;
            report.MissingTargetTables = report.Tables.Count(t => t.TargetExists == false);
            report.MismatchedRowCountTables = report.Tables.Count(t => t.SourceRowCount.HasValue && t.TargetRowCount.HasValue && t.SourceRowCount.Value != t.TargetRowCount.Value);

            await WriteComparisonReportFilesAsync(ctx, report, ct);
        }
        catch (Exception ex)
        {
            // Never fail the run for reporting; just log it.
            ctx.AppendLog($"[SourceTargetComparison][WARN] Failed to generate comparison report: {ex.Message}");
        }
        finally
        {
            // Update the single-page html snapshot if available.
            try { ctx.Engine.UpdateRunIndexHtml(ctx.RunDir); } catch { /* best-effort */ }
        }
    }

    private static async Task<List<(string Schema, string Table)>> ListSqlServerTablesAsync(SqlConnection openSql, CancellationToken ct)
    {
        const string sql = @"
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
ORDER BY TABLE_SCHEMA, TABLE_NAME";

        var list = new List<(string Schema, string Table)>();
        await using var cmd = new SqlCommand(sql, openSql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add((r.GetString(0), r.GetString(1)));
        }
        return list;
    }

    private static async Task<long> GetSqlRowCountAsync(SqlConnection openSql, string dbName, string schema, string table, CancellationToken ct)
    {
        // Use 3-part name to avoid reliance on current DB context.
        var sql = $"SELECT COUNT_BIG(1) FROM [{dbName}].[{schema}].[{table}]";
        await using var cmd = new SqlCommand(sql, openSql);
        var v = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(v);
    }

    private static async Task<long> GetOracleRowCountAsync(OracleConnection openOra, string schema, string table, bool preferUnquotedUpper, CancellationToken ct)
    {
        var sql = $"SELECT COUNT(1) FROM {OracleIdent.FormatSchema(schema)}.{OracleIdent.FormatObject(table, preferUnquotedUpper)}";
        await using var cmd = new OracleCommand(sql, openOra);
        var v = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(v);
    }

    private static async Task WriteComparisonReportFilesAsync(MigrationContext ctx, SourceTargetComparisonReport report, CancellationToken ct)
    {
        var jsonPath = Path.Combine(ctx.RunDir, "SourceTargetComparison_report.json");
        var txtPath = Path.Combine(ctx.RunDir, "SourceTargetComparison_report.txt");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8, ct);

        var sb = new StringBuilder();
        sb.AppendLine("Source vs Target Comparison");
        sb.AppendLine($"RunId: {report.RunId}");
        sb.AppendLine($"Source DB: {report.SourceDatabase}");
        sb.AppendLine($"Target Service: {report.TargetService}  PDB: {report.TargetPdb}");
        sb.AppendLine($"Started (UTC): {report.StartedUtc:O}");
        sb.AppendLine($"Completed (UTC): {report.CompletedUtc:O}");
        sb.AppendLine($"Duration: {report.DurationSeconds}s");
        sb.AppendLine();
        sb.AppendLine($"Total tables: {report.TotalTables}");
        sb.AppendLine($"Missing target tables: {report.MissingTargetTables}");
        sb.AppendLine($"Rowcount mismatches: {report.MismatchedRowCountTables}");
        sb.AppendLine();

        // Order mismatches first
        foreach (var t in report.Tables
                     .OrderByDescending(t => t.TargetExists == false)
                     .ThenByDescending(t => (t.SourceRowCount.HasValue && t.TargetRowCount.HasValue && t.SourceRowCount.Value != t.TargetRowCount.Value) ? 1 : 0)
                     .ThenBy(t => t.SourceSchema)
                     .ThenBy(t => t.SourceTable))
        {
            var src = t.SourceRowCount?.ToString() ?? $"ERR: {t.SourceRowCountError}";
            var tgt = t.TargetExists == false ? "MISSING" : (t.TargetRowCount?.ToString() ?? $"ERR: {t.TargetRowCountError}");
            sb.AppendLine($"{t.SourceSchema}.{t.SourceTable}  =>  {t.TargetSchema}.{t.TargetTable}  | src={src}  tgt={tgt}  delta={t.Delta}");
        }

        await File.WriteAllTextAsync(txtPath, sb.ToString(), Encoding.UTF8, ct);

        // Also persist to ToolMig artifacts best-effort
        try
        {
            if (ctx.Engine._toolMig is not null)
            {
                await ctx.Engine._toolMig.PutArtifactAsync(ctx.OpenSql, ctx.Summary.RunId, Path.GetFileName(txtPath), "text/plain", Encoding.UTF8.GetBytes(sb.ToString()), "Source vs Target comparison (TXT)", ct);
                await ctx.Engine._toolMig.PutArtifactAsync(ctx.OpenSql, ctx.Summary.RunId, Path.GetFileName(jsonPath), "application/json", Encoding.UTF8.GetBytes(json), "Source vs Target comparison (JSON)", ct);
            }
        }
        catch (Exception ex)
        {
            ctx.AppendLog($"[SourceTargetComparison][WARN] Failed to persist comparison report to ToolMig: {ex.Message}");
        }
    }

    internal sealed class SourceTargetComparisonReport
    {
        public Guid RunId { get; set; }
        public string? SourceDatabase { get; set; }
        public string? TargetService { get; set; }
        public string? TargetPdb { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public double DurationSeconds { get; set; }
        public int TotalTables { get; set; }
        public int MissingTargetTables { get; set; }
        public int MismatchedRowCountTables { get; set; }
        public List<SourceTargetTableComparison> Tables { get; set; } = new();
    }

    internal sealed class SourceTargetTableComparison
    {
        public string SourceSchema { get; set; } = "dbo";
        public string SourceTable { get; set; } = "";
        public string TargetSchema { get; set; } = "";
        public string TargetTable { get; set; } = "";

        public long? SourceRowCount { get; set; }
        public string? SourceRowCountError { get; set; }

        public bool? TargetExists { get; set; }
        public long? TargetRowCount { get; set; }
        public string? TargetRowCountError { get; set; }

        public long? Delta { get; set; }
    }
}
