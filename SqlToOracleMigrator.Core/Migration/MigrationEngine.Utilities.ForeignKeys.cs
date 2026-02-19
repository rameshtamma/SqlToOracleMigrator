using System;
using System.Collections.Generic;
using System.Linq;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    // StageError is intentionally private to MigrationEngine; keep this helper private as well.
    private async Task<List<StageError>> DeployForeignKeysAsync(
        OracleConnection openOra,
        IReadOnlyList<SqlForeignKeyDef> foreignKeys,
        Func<string, string> schemaMapper,
        bool enableNoValidate,
        bool preferUnquotedUppercaseIdentifiers,
        CancellationToken ct)
    {
        var errors = new List<StageError>();

        // Cache grants so we don't spam GRANT statements for the same (refOwner, refTable, grantee) tuple.
        var grantCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fk in foreignKeys)
        {
            string? ddl = null;

            ct.ThrowIfCancellationRequested();
            try
            {
                var parentSchema = schemaMapper(fk.TableSchema);
                var refSchema = schemaMapper(fk.RefTableSchema);

                var parentSchemaQ = OracleIdent.FormatSchema(parentSchema);
                var refSchemaQ = OracleIdent.FormatSchema(refSchema);
                var parentTableQ = OracleIdent.FormatObject(fk.TableName, preferUnquotedUppercaseIdentifiers);
                var refTableQ = OracleIdent.FormatObject(fk.RefTableName, preferUnquotedUppercaseIdentifiers);

                // Cross-schema FK creation requires the child schema to have REFERENCES on the parent table.
                // If we deploy constraints using an admin connection (SYS/PDBADMIN), Oracle still validates object privileges.
                // Without the grant, Oracle often reports ORA-00942 (table or view does not exist) for the referenced table.
                await EnsureFkGrantsAsync(openOra, refSchemaQ, refTableQ, parentSchemaQ, grantCache, ct);

                var fkNameQ = OracleIdent.FormatObject(fk.Name, preferUnquotedUppercaseIdentifiers);
                var parentCols = string.Join(",", fk.Columns.Select(c => OracleIdent.FormatObject(c.ColumnName, preferUnquotedUppercaseIdentifiers)));
                var refCols = string.Join(",", fk.Columns.Select(c => OracleIdent.FormatObject(c.RefColumnName, preferUnquotedUppercaseIdentifiers)));

                // Drop existing constraint (if any)
                var drop = $"BEGIN EXECUTE IMMEDIATE 'ALTER TABLE {parentSchemaQ}.{parentTableQ} DROP CONSTRAINT {fkNameQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
                await using (var dropCmd = new OracleCommand(drop, openOra))
                {
                    await dropCmd.ExecuteNonQueryAsync(ct);
                }

                var onDelete = (fk.OnDeleteAction ?? string.Empty).Trim();
                var onDeleteClause = onDelete.Equals("CASCADE", StringComparison.OrdinalIgnoreCase)
                    ? " ON DELETE CASCADE"
                    : onDelete.Equals("SET_NULL", StringComparison.OrdinalIgnoreCase) || onDelete.Equals("SET NULL", StringComparison.OrdinalIgnoreCase)
                        ? " ON DELETE SET NULL"
                        : string.Empty;

                var novalidate = enableNoValidate ? " ENABLE NOVALIDATE" : string.Empty;
                ddl = $"ALTER TABLE {parentSchemaQ}.{parentTableQ} ADD CONSTRAINT {fkNameQ} FOREIGN KEY ({parentCols}) REFERENCES {refSchemaQ}.{refTableQ} ({refCols}){onDeleteClause}{novalidate}";

                await using var cmd = new OracleCommand(ddl, openOra);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                errors.Add(StageError.FromException(MigrationStage.PostValidation, fk.TableSchema, fk.Name, ex));
                _logger.Warn($"[FK][WARN] Failed to deploy FK {fk.TableSchema}.{fk.Name}: {ex.Message}. DDL={(ddl ?? "(null)")}");
            }
        }
        return errors;
    }

    private static async Task EnsureFkGrantsAsync(
        OracleConnection openOra,
        string refSchemaQ,
        string refTableQ,
        string granteeSchemaQ,
        HashSet<string> cache,
        CancellationToken ct)
    {
        // Normalize cache key without quotes for stability.
        var key = $"{refSchemaQ}.{refTableQ}->{granteeSchemaQ}";
        if (!cache.Add(key)) return;

        // Best-effort GRANTs (ignore errors; the subsequent FK DDL will surface any real problems).
        // REFERENCES is required. SELECT is sometimes required by certain validation paths and makes diagnostics easier.
        var grantRefs = $"BEGIN EXECUTE IMMEDIATE 'GRANT REFERENCES ON {refSchemaQ}.{refTableQ} TO {granteeSchemaQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
        var grantSel = $"BEGIN EXECUTE IMMEDIATE 'GRANT SELECT ON {refSchemaQ}.{refTableQ} TO {granteeSchemaQ}'; EXCEPTION WHEN OTHERS THEN NULL; END;";

        await using (var cmd = new OracleCommand(grantRefs, openOra))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new OracleCommand(grantSel, openOra))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
