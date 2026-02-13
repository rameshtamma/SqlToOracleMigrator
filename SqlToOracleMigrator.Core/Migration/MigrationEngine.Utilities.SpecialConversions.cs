using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private async Task<List<StageError>> ConvertSpatialAndXmlAsync(MigrationContext ctx, CancellationToken ct)
    {

        if (!ctx.Request.EnableSpatialXmlStaging) return new List<StageError>();

        var errors = new List<StageError>();

        foreach (var (schema, table) in ctx.Tables)
        {
            ct.ThrowIfCancellationRequested();
            var targetSchema = ctx.GetTargetSchema(schema);

            try
            {
                await ConvertSpatialAndXmlForTableAsync(ctx.OpenOra, targetSchema, table, ct);

                // After successful conversion, enforce NOT NULL for special columns that were NOT NULL in source.
                // This is Rule (A): "nullable-initial special-type" — keep nullable during load, enforce after conversion.
                await EnforceSpecialTypeNotNullsAsync(ctx, schema, table, targetSchema, ct);

                if (ctx.Request.KeepStagingColumnsOnlyOnFailure)
                {
                    // Option 3: drop staging columns only if conversion succeeded.
                    await DropSpatialXmlStagingColumnsAsync(ctx.OpenOra, targetSchema, table, ct);
                }
            }
            catch (Exception ex)
            {
                // Option 3: keep staging columns on failure so the user can diagnose and retry.
                errors.Add(StageError.FromException(MigrationStage.PostValidation, schema, table, ex));
                ctx.AppendLog($"[PostValidation][WARN] Stage 9 conversion failed for {schema}.{table}: {ex.Message}");
                _logger.Warn($"Stage 9 conversion failed for {schema}.{table}: {ex.Message}");

                if (ctx.StageMode == ErrorHandlingMode.FailFast)
                    throw;
            }
        }

        // If user asked to keep staging always, do nothing further.
        return errors;

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

    
    /// <summary>
    /// Rule (A): special-type base columns (XMLTYPE / SDO_GEOMETRY) are created nullable for load,
    /// then enforced NOT NULL after conversion IF they were NOT NULL in SQL Server and contain no NULLs in Oracle.
    /// </summary>
    private async Task EnforceSpecialTypeNotNullsAsync(MigrationContext ctx, string sourceSchema, string table, string targetSchema, CancellationToken ct)
    {
        // Identify XML / spatial columns from SQL Server and their nullability.
        const string sql = @"
SELECT c.name AS ColumnName,
       t.name AS TypeName,
       c.is_nullable AS IsNullable
  FROM sys.columns c
  JOIN sys.types t ON c.user_type_id = t.user_type_id
  JOIN sys.tables tb ON c.object_id = tb.object_id
  JOIN sys.schemas s ON tb.schema_id = s.schema_id
 WHERE s.name = @p_schema
   AND tb.name = @p_table
   AND t.name IN ('xml', 'geography', 'geometry')
 ORDER BY c.column_id;";

        var specials = new List<(string Col, string Type, bool IsNullable)>();
        await using (var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, ctx.OpenSql))
        {
            cmd.Parameters.AddWithValue("@p_schema", sourceSchema);
            cmd.Parameters.AddWithValue("@p_table", table);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var col = r.GetString(0);
                var typ = r.GetString(1);
                var isNullable = r.GetBoolean(2);
                specials.Add((col, typ, isNullable));
            }
        }

        // Only enforce for source NOT NULL columns.
        var toEnforce = specials.Where(x => !x.IsNullable).ToList();
        if (toEnforce.Count == 0) return;

        var schemaPrefix = OracleIdent.FormatSchema(targetSchema);
        var tableName = OracleIdent.QuoteIdent(table);

        foreach (var (col, _, _) in toEnforce)
        {
            ct.ThrowIfCancellationRequested();

            // Ensure the column exists and is currently nullable in Oracle.
            const string colMeta = @"
SELECT NULLABLE
  FROM ALL_TAB_COLUMNS
 WHERE OWNER = :p_owner AND TABLE_NAME = :p_table AND COLUMN_NAME = :p_col";
            string? nullableFlag = null;
            await using (var c = new OracleCommand(colMeta, ctx.OpenOra) { BindByName = true })
            {
                c.Parameters.Add(new OracleParameter("p_owner", OracleDbType.Varchar2, targetSchema.ToUpperInvariant(), System.Data.ParameterDirection.Input));
                c.Parameters.Add(new OracleParameter("p_table", OracleDbType.Varchar2, table.ToUpperInvariant(), System.Data.ParameterDirection.Input));
                c.Parameters.Add(new OracleParameter("p_col", OracleDbType.Varchar2, col.ToUpperInvariant(), System.Data.ParameterDirection.Input));
                var o = await c.ExecuteScalarAsync(ct);
                nullableFlag = o?.ToString();
            }

            if (string.IsNullOrWhiteSpace(nullableFlag) || nullableFlag.Equals("N", System.StringComparison.OrdinalIgnoreCase))
                continue; // doesn't exist or already NOT NULL

            // Only enforce if there are no NULLs (otherwise ALTER will fail).
            var nullCountSql = $"SELECT COUNT(*) FROM {schemaPrefix}.{tableName} WHERE {OracleIdent.QuoteIdent(col)} IS NULL";
            long nullCount = 0;
            await using (var n = new OracleCommand(nullCountSql, ctx.OpenOra) { CommandTimeout = 0 })
            {
                var o = await n.ExecuteScalarAsync(ct);
                if (o != null && o != System.DBNull.Value)
                    nullCount = System.Convert.ToInt64(o);
            }

            if (nullCount != 0)
            {
                ctx.AppendLog($"[PostValidation][INFO] Skipping NOT NULL enforcement for {sourceSchema}.{table}.{col} because {nullCount} NULL(s) exist after conversion.");
                continue;
            }

            var alter = $"ALTER TABLE {schemaPrefix}.{tableName} MODIFY ({OracleIdent.QuoteIdent(col)} NOT NULL)";
            await using var a = new OracleCommand(alter, ctx.OpenOra) { CommandTimeout = 0 };
            await a.ExecuteNonQueryAsync(ct);

            ctx.AppendLog($"[PostValidation][OK] Enforced NOT NULL for special column {sourceSchema}.{table}.{col} (Rule A).");
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
