using Microsoft.Data.SqlClient;
using System.Globalization;
using SqlToOracleMigrator.Core.Scoring;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlToOracleMigrator.Core;

public sealed class SqlServerMetadataProvider
{
    private readonly ISqlQueryStore _queries;
    private readonly IAppLogger _logger;

    public SqlServerMetadataProvider(ISqlQueryStore queries, IAppLogger logger)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    

private static double? ToNullableDouble(SqlDataReader rdr, int ordinal)
{
    if (ordinal < 0) return null;
    if (rdr.IsDBNull(ordinal)) return null;

    object v = rdr.GetValue(ordinal);
    try
    {
        return v switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            string str when double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => Convert.ToDouble(v, CultureInfo.InvariantCulture)
        };
    }
    catch
    {
        return null;
    }
}

public async Task<IReadOnlyList<string>> ListDatabasesAsync(SqlConnection openConnection, CancellationToken cancellationToken)
    {
        if (openConnection is null) throw new ArgumentNullException(nameof(openConnection));

        var sql = _queries.Get("ListSqlDatabases");
        await using var cmd = new SqlCommand(sql, openConnection);
        var list = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var name = rdr.GetString(0);
            list.Add(name);
        }
        return list;
    }

    public async Task<InventoryDbSummary> GetDbSummaryAsync(SqlConnection openConnection, string dbName, CancellationToken cancellationToken)
    {
        if (openConnection is null) throw new ArgumentNullException(nameof(openConnection));
        if (string.IsNullOrWhiteSpace(dbName)) throw new ArgumentNullException(nameof(dbName));

        var db = SqlIdent.Bracket(dbName);
        var replacements = new Dictionary<string, string> { ["db"] = db };

        var sql = _queries.Format("SqlDbSummary", replacements);
        await using var cmd = new SqlCommand(sql, openConnection);
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);

        var summary = new InventoryDbSummary
        {
            Side = "Source",
            Engine = "SQL Server",
            DatabaseOrService = dbName,
            DefaultSchemaOrUser = "dbo"
        };

        if (await rdr.ReadAsync(cancellationToken))
        {
            summary.DatabaseSizeGb = ToNullableDouble(rdr, 0);
            summary.DataSizeGb = ToNullableDouble(rdr, 1);
            summary.LogOrRedoSizeGb = ToNullableDouble(rdr, 2);

            summary.SchemaCount = rdr.IsDBNull(3) ? null : rdr.GetInt32(3);
            summary.TableCount = rdr.IsDBNull(4) ? null : rdr.GetInt32(4);
            summary.ViewCount = rdr.IsDBNull(5) ? null : rdr.GetInt32(5);
            summary.ProcedureCount = rdr.IsDBNull(6) ? null : rdr.GetInt32(6);
            summary.FunctionCount = rdr.IsDBNull(7) ? null : rdr.GetInt32(7);
            summary.SequenceCount = rdr.IsDBNull(8) ? null : rdr.GetInt32(8);
            summary.SynonymCount = rdr.IsDBNull(9) ? null : rdr.GetInt32(9);
            summary.TriggerCount = rdr.IsDBNull(10) ? null : rdr.GetInt32(10);
            summary.IndexCount = rdr.IsDBNull(11) ? null : rdr.GetInt32(11);
            summary.LastStatsUpdate = rdr.IsDBNull(12) ? null : GetDateTimeOffset(rdr, 12);
        }

        return summary;
    }

    public async Task<(IReadOnlyList<InventoryObjectSummary> items, bool hasMore)> ListObjectsPagedAsync(
        SqlConnection openConnection,
        string dbName,
        int offset,
        int fetch,
        CancellationToken cancellationToken)
    {
        if (openConnection is null) throw new ArgumentNullException(nameof(openConnection));
        if (string.IsNullOrWhiteSpace(dbName)) throw new ArgumentNullException(nameof(dbName));
        fetch = Math.Clamp(fetch, 1, 5000);
        offset = Math.Max(0, offset);

        var db = SqlIdent.Bracket(dbName);
        var replacements = new Dictionary<string, string>
        {
            ["db"] = db
        };

        var sql = _queries.Format("ListSqlObjectsPaged", replacements);
        await using var cmd = new SqlCommand(sql, openConnection);
        cmd.Parameters.AddWithValue("@Offset", offset);
        cmd.Parameters.AddWithValue("@Fetch", fetch);

        var list = new List<InventoryObjectSummary>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var item = new InventoryObjectSummary
            {
                Schema = rdr.GetString(0),
                ObjectName = rdr.GetString(1),
                ObjectType = rdr.GetString(2),
                CreatedDate = rdr.IsDBNull(3) ? null : GetDateTimeOffset(rdr, 3),
                LastModifiedDate = rdr.IsDBNull(4) ? null : GetDateTimeOffset(rdr, 4),
                EstimatedRows = rdr.IsDBNull(5) ? null : rdr.GetInt64(5),
                EstimatedSizeMb = ToNullableDouble(rdr, 6),
                DependsOnCount = rdr.IsDBNull(7) ? null : rdr.GetInt32(7),
                DependedByCount = rdr.IsDBNull(8) ? null : rdr.GetInt32(8),
                ComplexityScore = rdr.IsDBNull(9) ? 1 : rdr.GetInt32(9),
                MigrationStatus = "Not started"
            };
            item.ComplexityScore = ComplexityScorer.Score(item.ObjectType, item.EstimatedRows, item.EstimatedSizeMb, item.DependsOnCount, item.DependedByCount);
            list.Add(item);
        }

        // Determine hasMore with one extra fetch
        var hasMore = list.Count == fetch;
        return (list, hasMore);
    }

    public async Task<IReadOnlyList<SqlTableColumn>> GetTableColumnsAsync(SqlConnection openConnection, string dbName, string schema, string table, CancellationToken cancellationToken)
    {
        if (openConnection is null) throw new ArgumentNullException(nameof(openConnection));

        var db = SqlIdent.Bracket(dbName);
        var replacements = new Dictionary<string, string> { ["db"] = db };
        var sql = _queries.Format("GetSqlTableColumnMetadata", replacements);

        await using var cmd = new SqlCommand(sql, openConnection);
        cmd.Parameters.AddWithValue("@SchemaName", schema);
        cmd.Parameters.AddWithValue("@TableName", table);

        var columns = new List<SqlTableColumn>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            columns.Add(new SqlTableColumn
            {
                ColumnName = rdr.GetString(0),
                SqlTypeName = rdr.GetString(1),
                MaxLength = GetNullableInt32(rdr, 2),
                Precision = GetNullableInt32(rdr, 3),
                Scale = GetNullableInt32(rdr, 4),
                IsNullable = rdr.GetBoolean(5),
                Ordinal = rdr.GetInt32(6),
                DefaultDefinition = rdr.IsDBNull(7) ? null : rdr.GetString(7)
            });
        }

        return columns;
    }

    private static DateTimeOffset GetDateTimeOffset(SqlDataReader rdr, int ordinal)
{
    var v = rdr.GetValue(ordinal);
    if (v is DateTimeOffset dto) return dto;
    if (v is DateTime dt) return new DateTimeOffset(dt);
    // Fallback conversion
    return new DateTimeOffset(Convert.ToDateTime(v, CultureInfo.InvariantCulture));
}

private static int? GetNullableInt32(SqlDataReader rdr, int ordinal)
{
    if (rdr.IsDBNull(ordinal)) return null;
    var v = rdr.GetValue(ordinal);
    return v switch
    {
        int i => i,
        short s => s,
        byte b => b,
        long l => checked((int)l),
        decimal d => (int)d,
        _ => Convert.ToInt32(v, CultureInfo.InvariantCulture)
    };
}

public async Task<long> GetTableRowCountAsync(SqlConnection openConnection, string dbName, string schema, string table, CancellationToken cancellationToken)
    {
        var db = SqlIdent.Bracket(dbName);
        var replacements = new Dictionary<string, string> { ["db"] = db };
        var sql = _queries.Format("SqlTableRowCount", replacements);

        await using var cmd = new SqlCommand(sql, openConnection);
        cmd.Parameters.AddWithValue("@SchemaName", schema);
        cmd.Parameters.AddWithValue("@TableName", table);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }
}

public sealed class SqlTableColumn
{
    public string ColumnName { get; set; } = "";
    public string SqlTypeName { get; set; } = "";
    public int? MaxLength { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public bool IsNullable { get; set; }
    public int Ordinal { get; set; }
    public string? DefaultDefinition { get; set; }
}