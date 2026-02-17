using Microsoft.Data.SqlClient;
using SqlToOracleMigrator.Core;

namespace SqlToOracleMigrator.Core.Migration.DataPrep;

public sealed class GoldenRowSampler
{
    private readonly SqlServerMetadataProvider _sqlMeta;

    public GoldenRowSampler(SqlServerMetadataProvider sqlMeta)
    {
        _sqlMeta = sqlMeta ?? throw new ArgumentNullException(nameof(sqlMeta));
    }

    public async Task<TableSampleSummary> SampleTopAsync(
        SqlConnection openSql,
        string dbName,
        string schema,
        string table,
        int topN,
        CancellationToken ct)
    {
        var cols = await _sqlMeta.GetTableColumnsAsync(openSql, dbName, schema, table, ct);
        var summary = new TableSampleSummary();

        if (cols.Count == 0 || topN <= 0)
            return summary;

        var selectList = string.Join(",", cols.OrderBy(c => c.Ordinal).Select(c => SqlIdent.Bracket(c.ColumnName)));
        var from = $"{SqlIdent.Bracket(schema)}.{SqlIdent.Bracket(table)}";

        // Note: no ORDER BY here by design; we want a lightweight sample.
        var sql = $"SELECT TOP ({topN}) {selectList} FROM {from};";

        await using var cmd = new SqlCommand(sql, openSql) { CommandTimeout = 0 };
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var ordToCol = cols.OrderBy(c => c.Ordinal).ToList();
        while (await rdr.ReadAsync(ct))
        {
            summary.SampledRows++;

            for (var i = 0; i < rdr.FieldCount && i < ordToCol.Count; i++)
            {
                var c = ordToCol[i];
                var isNull = await rdr.IsDBNullAsync(i, ct);

                if (!c.IsNullable && isNull)
                {
                    summary.NotNullViolations++;
                    continue;
                }

                if (isNull) continue;

                // Strings: track max observed length.
                if (IsStringType(c.SqlTypeName))
                {
                    var s = Convert.ToString(rdr.GetValue(i));
                    if (s is null) continue;
                    var len = s.Length;

                    if (!summary.MaxStringLengthByColumn.TryGetValue(c.ColumnName, out var curr) || len > curr)
                        summary.MaxStringLengthByColumn[c.ColumnName] = len;
                }

                // Date/time: track min/max; warn if outside Oracle DATE range.
                if (IsDateType(c.SqlTypeName))
                {
                    DateTime dt;
                    try
                    {
                        dt = Convert.ToDateTime(rdr.GetValue(i));
                    }
                    catch
                    {
                        summary.DateParseWarnings++;
                        continue;
                    }

                    if (dt.Year < 1 || dt.Year > 9999)
                        summary.DateParseWarnings++;

                    summary.MinDate = summary.MinDate is null ? dt : (dt < summary.MinDate ? dt : summary.MinDate);
                    summary.MaxDate = summary.MaxDate is null ? dt : (dt > summary.MaxDate ? dt : summary.MaxDate);
                }
            }
        }

        return summary;
    }

    private static bool IsStringType(string sqlType)
        => sqlType.Equals("varchar", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("char", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("nchar", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("text", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("ntext", StringComparison.OrdinalIgnoreCase);

    private static bool IsDateType(string sqlType)
        => sqlType.Equals("datetime", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("datetime2", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("smalldatetime", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("date", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("time", StringComparison.OrdinalIgnoreCase)
           || sqlType.Equals("datetimeoffset", StringComparison.OrdinalIgnoreCase);
}
