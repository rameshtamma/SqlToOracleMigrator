using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core.Oracle;

namespace SqlToOracleMigrator.Core.Migration.DataPrep;

public sealed class OracleStagingPreparer
{
    private readonly OracleDdlExecutor _ddl;

    public OracleStagingPreparer(OracleConnection openOra)
    {
        _ddl = new OracleDdlExecutor(openOra);
    }

    public async Task EnsureStagingForTableAsync(
        string targetSchema,
        string table,
        IReadOnlyList<(string ColumnName, string SqlTypeName, bool IsNullable)> sourceColumns,
        bool relaxNotNullOnStagedColumns,
        CancellationToken ct)
    {
        // Stage policy:
        // - XML: add <col>__XML CLOB
        // - Spatial: add <col>__WKB BLOB, <col>__SRID NUMBER(10)
        // - Relax NOT NULL on main column to allow staging load; stage 9 will enforce.

        var schemaQ = OracleIdent.FormatSchema(targetSchema);
        var tableQ = OracleIdent.QuoteIdent(table);

        foreach (var c in sourceColumns)
        {
            var colName = c.ColumnName;

            if (c.SqlTypeName.Equals("xml", StringComparison.OrdinalIgnoreCase))
            {
                var stageCol = OracleIdent.QuoteIdent(colName + "__XML");
                await _ddl.ExecuteIdempotentAsync($"ALTER TABLE {schemaQ}.{tableQ} ADD ({stageCol} CLOB)", ct);

                if (relaxNotNullOnStagedColumns)
                {
                    // Make main column nullable (it will be populated in Stage 9).
                    var mainCol = OracleIdent.QuoteIdent(colName);
                    await _ddl.ExecuteIdempotentAsync($"ALTER TABLE {schemaQ}.{tableQ} MODIFY ({mainCol} NULL)", ct);
                }
            }

            if (c.SqlTypeName.Equals("geography", StringComparison.OrdinalIgnoreCase) || c.SqlTypeName.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                var wkb = OracleIdent.QuoteIdent(colName + "__WKB");
                var srid = OracleIdent.QuoteIdent(colName + "__SRID");

                await _ddl.ExecuteIdempotentAsync($"ALTER TABLE {schemaQ}.{tableQ} ADD ({wkb} BLOB)", ct);
                await _ddl.ExecuteIdempotentAsync($"ALTER TABLE {schemaQ}.{tableQ} ADD ({srid} NUMBER(10))", ct);

                if (relaxNotNullOnStagedColumns)
                {
                    var mainCol = OracleIdent.QuoteIdent(colName);
                    await _ddl.ExecuteIdempotentAsync($"ALTER TABLE {schemaQ}.{tableQ} MODIFY ({mainCol} NULL)", ct);
                }
            }
        }
    }
}
