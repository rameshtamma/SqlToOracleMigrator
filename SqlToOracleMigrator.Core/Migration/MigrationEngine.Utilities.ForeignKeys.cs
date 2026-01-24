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
        CancellationToken ct)
    {
        var errors = new List<StageError>();
        foreach (var fk in foreignKeys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var parentSchema = schemaMapper(fk.TableSchema);
                var refSchema = schemaMapper(fk.RefTableSchema);

                var parentSchemaQ = OracleIdent.FormatSchema(parentSchema);
                var refSchemaQ = OracleIdent.FormatSchema(refSchema);
                var parentTableQ = OracleIdent.QuoteIdent(fk.TableName);
                var refTableQ = OracleIdent.QuoteIdent(fk.RefTableName);

                var fkNameQ = OracleIdent.QuoteIdent(fk.Name);
                var parentCols = string.Join(",", fk.Columns.Select(c => OracleIdent.QuoteIdent(c.ColumnName)));
                var refCols = string.Join(",", fk.Columns.Select(c => OracleIdent.QuoteIdent(c.RefColumnName)));

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
                var ddl = $"ALTER TABLE {parentSchemaQ}.{parentTableQ} ADD CONSTRAINT {fkNameQ} FOREIGN KEY ({parentCols}) REFERENCES {refSchemaQ}.{refTableQ} ({refCols}){onDeleteClause}{novalidate}";

                await using var cmd = new OracleCommand(ddl, openOra);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                errors.Add(StageError.FromException(MigrationStage.PostValidation, fk.TableSchema, fk.Name, ex));
                _logger.Warn($"[FK][WARN] Failed to deploy FK {fk.TableSchema}.{fk.Name}: {ex.Message}");
            }
        }
        return errors;
    }
}
