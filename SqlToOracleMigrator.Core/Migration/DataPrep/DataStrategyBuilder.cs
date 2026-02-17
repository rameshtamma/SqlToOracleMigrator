using SqlToOracleMigrator.Core;

namespace SqlToOracleMigrator.Core.Migration.DataPrep;

public sealed class DataStrategyBuilder
{
    private readonly SqlServerMetadataProvider _sqlMeta;

    public DataStrategyBuilder(SqlServerMetadataProvider sqlMeta)
    {
        _sqlMeta = sqlMeta ?? throw new ArgumentNullException(nameof(sqlMeta));
    }

    public async Task<TableStrategy> BuildForTableAsync(
        Microsoft.Data.SqlClient.SqlConnection openSql,
        string dbName,
        string schema,
        string table,
        TableSampleSummary sample,
        CancellationToken ct)
    {
        var cols = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, ct);

        var requiresXml = cols.Any(c => c.SqlTypeName.Equals("xml", StringComparison.OrdinalIgnoreCase));
        var requiresSpatial = cols.Any(c => c.SqlTypeName.Equals("geography", StringComparison.OrdinalIgnoreCase)
                                         || c.SqlTypeName.Equals("geometry", StringComparison.OrdinalIgnoreCase));

        // Heuristic: BulkCopy works for most cases, but spatial/XML often benefits from staging.
        // If there are not-null violations in sample, keep BulkCopy but enable default policy.
        var useBulk = true;

        // If the table has unsupported/rare types, consider fallback.
        if (cols.Any(c => c.SqlTypeName.Equals("sql_variant", StringComparison.OrdinalIgnoreCase)))
            useBulk = false;

        return new TableStrategy
        {
            Schema = schema,
            Table = table,
            UseBulkCopy = useBulk,
            RequiresXmlStaging = requiresXml,
            RequiresSpatialStaging = requiresSpatial,
            ApplyNotNullDefaultPolicy = sample.NotNullViolations > 0,
            RelaxNotNullOnStagedColumns = (requiresXml || requiresSpatial),
            Sample = sample
        };
    }
}
