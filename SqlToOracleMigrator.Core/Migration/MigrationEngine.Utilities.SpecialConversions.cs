using System.Linq;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private async Task ConvertSpatialAndXmlAsync(MigrationContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.EnableSpatialXmlStaging) return;

        foreach (var (schema, table) in ctx.Tables)
        {
            ct.ThrowIfCancellationRequested();
            var targetSchema = ctx.GetTargetSchema(schema);

            try
            {
                await ConvertSpatialAndXmlForTableAsync(ctx.OpenOra, targetSchema, table, ct);

                if (ctx.Request.KeepStagingColumnsOnlyOnFailure)
                {
                    // Drop staging columns only if conversion succeeded.
                    await DropSpatialXmlStagingColumnsAsync(ctx.OpenOra, targetSchema, table, ct);
                }
            }
            catch
            {
                // Option 1: Fail-fast on conversion failure. Keep staging columns to allow diagnosis.
                throw;
            }
        }
    }

    private static async Task ConvertSpatialAndXmlForTableAsync(OracleConnection openOra, string targetSchema, string table, CancellationToken ct)
    {
        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var tableName = OracleIdent.QuoteIdent(table);

        // XMLTYPE conversion: any column ending with __XML indicates staging CLOB for XMLTYPE.
        // Spatial conversion: any column ending with __WKB indicates staging BLOB for SDO_GEOMETRY.
        // Note: We keep this conversion generic by reading USER_TAB_COLS.
        const string colSql = @"
SELECT COLUMN_NAME
  FROM ALL_TAB_COLUMNS
 WHERE OWNER = :p_owner
   AND TABLE_NAME = :p_table
   AND (COLUMN_NAME LIKE '%\_\_XML' ESCAPE '\' OR COLUMN_NAME LIKE '%\_\_WKB' ESCAPE '\')
 ORDER BY COLUMN_NAME";

        var stagingCols = new List<string>();
        await using (var cmd = new OracleCommand(colSql, openOra))
        {
            cmd.BindByName = true;
            cmd.Parameters.Add(new OracleParameter("p_owner", OracleDbType.Varchar2, targetSchema.ToUpperInvariant(), System.Data.ParameterDirection.Input));
            cmd.Parameters.Add(new OracleParameter("p_table", OracleDbType.Varchar2, table.ToUpperInvariant(), System.Data.ParameterDirection.Input));

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                stagingCols.Add(r.GetString(0));
            }
        }

        foreach (var sc in stagingCols)
        {
            if (sc.EndsWith("__XML", StringComparison.OrdinalIgnoreCase))
            {
                var baseCol = sc[..^5]; // remove __XML
                var sql = $"UPDATE {schemaPrefix}.{tableName} SET {OracleIdent.QuoteIdent(baseCol)} = XMLTYPE({OracleIdent.QuoteIdent(sc)}) WHERE {OracleIdent.QuoteIdent(sc)} IS NOT NULL";
                await using var u = new OracleCommand(sql, openOra) { CommandTimeout = 0 };
                await u.ExecuteNonQueryAsync(ct);
            }
            else if (sc.EndsWith("__WKB", StringComparison.OrdinalIgnoreCase))
            {
                var baseCol = sc[..^5]; // remove __WKB
                var sridCol = baseCol + "__SRID";
                var sql = $"UPDATE {schemaPrefix}.{tableName} SET {OracleIdent.QuoteIdent(baseCol)} = SDO_UTIL.FROM_WKBGEOMETRY({OracleIdent.QuoteIdent(sc)}) WHERE {OracleIdent.QuoteIdent(sc)} IS NOT NULL";
                await using var u = new OracleCommand(sql, openOra) { CommandTimeout = 0 };
                await u.ExecuteNonQueryAsync(ct);

                // Apply SRID best-effort if SRID staging column exists.
                var sridUpdate = $@"
DECLARE
  c NUMBER;
BEGIN
  SELECT COUNT(*) INTO c
    FROM ALL_TAB_COLUMNS
   WHERE OWNER = :p_owner AND TABLE_NAME = :p_table AND COLUMN_NAME = :p_col;
  IF c > 0 THEN
    EXECUTE IMMEDIATE 'UPDATE {schemaPrefix}.{tableName} SET {OracleIdent.QuoteIdent(baseCol)}.SDO_SRID = {OracleIdent.QuoteIdent(sridCol)} WHERE {OracleIdent.QuoteIdent(sridCol)} IS NOT NULL';
  END IF;
END;";
                await using var s = new OracleCommand(sridUpdate, openOra) { BindByName = true, CommandTimeout = 0 };
                s.Parameters.Add(new OracleParameter("p_owner", OracleDbType.Varchar2, targetSchema.ToUpperInvariant(), System.Data.ParameterDirection.Input));
                s.Parameters.Add(new OracleParameter("p_table", OracleDbType.Varchar2, table.ToUpperInvariant(), System.Data.ParameterDirection.Input));
                s.Parameters.Add(new OracleParameter("p_col", OracleDbType.Varchar2, sridCol.ToUpperInvariant(), System.Data.ParameterDirection.Input));
                await s.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static async Task DropSpatialXmlStagingColumnsAsync(OracleConnection openOra, string targetSchema, string table, CancellationToken ct)
    {
        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var tableName = OracleIdent.QuoteIdent(table);

        // Drop all __XML, __WKB, __SRID columns if present. Keep if drop fails (best effort).
        var dropSql = $@"
DECLARE
  PROCEDURE drop_col(p_col VARCHAR2) IS
  BEGIN
    EXECUTE IMMEDIATE 'ALTER TABLE {schemaPrefix}.{tableName} DROP COLUMN ' || p_col;
  EXCEPTION WHEN OTHERS THEN NULL;
  END;
BEGIN
  FOR c IN (
    SELECT COLUMN_NAME
      FROM ALL_TAB_COLUMNS
     WHERE OWNER = :p_owner
       AND TABLE_NAME = :p_table
       AND (COLUMN_NAME LIKE '%\_\_XML' ESCAPE '\'
         OR COLUMN_NAME LIKE '%\_\_WKB' ESCAPE '\'
         OR COLUMN_NAME LIKE '%\_\_SRID' ESCAPE '\')
  ) LOOP
    drop_col(c.COLUMN_NAME);
  END LOOP;
END;";
        await using var cmd = new OracleCommand(dropSql, openOra) { BindByName = true, CommandTimeout = 0 };
        cmd.Parameters.Add(new OracleParameter("p_owner", OracleDbType.Varchar2, targetSchema.ToUpperInvariant(), System.Data.ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("p_table", OracleDbType.Varchar2, table.ToUpperInvariant(), System.Data.ParameterDirection.Input));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task GatherSchemaStatsAsync(MigrationContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.GatherSchemaStats) return;

        // Best effort - if the user lacks privileges, log and continue.
        try
        {
            var schemas = ctx.Tables.Select(t => ctx.GetTargetSchema(t.Schema)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var s in schemas)
            {
                var plsql = "BEGIN DBMS_STATS.GATHER_SCHEMA_STATS(ownname => :p_owner, options => 'GATHER AUTO'); END;";
                await using var cmd = new OracleCommand(plsql, ctx.OpenOra) { BindByName = true, CommandTimeout = 0 };
                cmd.Parameters.Add(new OracleParameter("p_owner", OracleDbType.Varchar2, s.ToUpperInvariant(), System.Data.ParameterDirection.Input));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"DBMS_STATS.GATHER_SCHEMA_STATS failed (best effort): {ex.Message}");
            ctx.AppendLog($"[PostLoadEnforcement][WARN] Stats gather failed: {ex.Message}");
        }
    }
}
