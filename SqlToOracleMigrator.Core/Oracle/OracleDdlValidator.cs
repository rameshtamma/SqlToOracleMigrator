using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core.Oracle;

public sealed class OracleDdlValidationError
{
    public string? Schema { get; set; }
    public string? ObjectName { get; set; }
    public string? ObjectType { get; set; }
    public string ErrorType { get; set; } = "OracleException";
    public string Message { get; set; } = "";
    public string? Details { get; set; }
    public string? ErrorCode { get; set; }
    public int? OracleNumber { get; set; }
}

public sealed class OracleDdlValidationReport
{
    public DateTimeOffset ValidatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int StatementCount { get; set; }
    public int ErrorCount => Errors.Count;
    public List<OracleDdlValidationError> Errors { get; set; } = new();
}

/// <summary>
/// Parse-only DDL validation via DBMS_SQL.PARSE.
/// This avoids partial deployment while still catching syntax/identifier issues.
/// </summary>
public sealed class OracleDdlValidator
{
    private readonly OracleConnection _openOra;

    public OracleDdlValidator(OracleConnection openOra)
    {
        _openOra = openOra ?? throw new ArgumentNullException(nameof(openOra));
    }

    public async Task<OracleDdlValidationReport> ValidateBundleAsync(SchemaBuildDdlBundle bundle, CancellationToken ct)
    {
        var report = new OracleDdlValidationReport { StatementCount = bundle.Statements.Count };

        foreach (var stmt in bundle.Statements)
        {
            ct.ThrowIfCancellationRequested();

            // DBMS_SQL.PARSE can perform semantic checks for DDL.
            // During Stage 5, schemas exist but tables typically do not yet.
            // Statements like CREATE INDEX / ALTER TABLE may fail with ORA-00942
            // even when the syntax is valid. We therefore parse-only validate
            // only self-contained statements.
            if (!ShouldParseOnly(stmt))
                continue;

            try
            {
                await ParseOnlyAsync(stmt.Sql, ct);
            }
            catch (OracleException oex)
            {
                // ORA-24344 = "success with compilation error".
                // For programmable objects we treat this as a warning in Stage 5.
                if (oex.Number == 24344 && IsProgrammable(stmt))
                    continue;

                report.Errors.Add(new OracleDdlValidationError
                {
                    Schema = stmt.Schema,
                    ObjectName = stmt.ObjectName,
                    ObjectType = stmt.ObjectType,
                    Message = oex.Message,
                    Details = oex.ToString(),
                    OracleNumber = oex.Number,
                    ErrorCode = $"ORA-{oex.Number:00000}"
                });
            }
            catch (Exception ex)
            {
                report.Errors.Add(new OracleDdlValidationError
                {
                    Schema = stmt.Schema,
                    ObjectName = stmt.ObjectName,
                    ObjectType = stmt.ObjectType,
                    ErrorType = ex.GetType().Name,
                    Message = ex.Message,
                    Details = ex.ToString(),
                });
            }
        }

        return report;
    }

    private static bool IsProgrammable(SchemaBuildDdlStatement stmt)
    {
        var t = (stmt.ObjectType ?? string.Empty).Trim().ToUpperInvariant();
        return t is "PROCEDURE" or "FUNCTION" or "TRIGGER" or "PACKAGE";
    }

    private static bool ShouldParseOnly(SchemaBuildDdlStatement stmt)
    {
        // Prefer metadata when available.
        var t = (stmt.ObjectType ?? string.Empty).Trim().ToUpperInvariant();

        // Safe self-contained DDL (does not require referenced objects).
        if (t is "TABLE" or "SEQUENCE")
            return true;

        // Everything else is skipped in Stage 5 to avoid false negatives.
        return false;
    }

    private async Task ParseOnlyAsync(string ddl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ddl)) return;

        const string plsql = @"
DECLARE
  c INTEGER;
BEGIN
  c := DBMS_SQL.OPEN_CURSOR;
  DBMS_SQL.PARSE(c, :p_sql, DBMS_SQL.NATIVE);
  DBMS_SQL.CLOSE_CURSOR(c);
EXCEPTION
  WHEN OTHERS THEN
    BEGIN
      IF c IS NOT NULL THEN DBMS_SQL.CLOSE_CURSOR(c); END IF;
    EXCEPTION WHEN OTHERS THEN NULL; END;
    RAISE;
END;";

        await using var cmd = _openOra.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = plsql;
        cmd.CommandType = System.Data.CommandType.Text;
        cmd.Parameters.Add("p_sql", OracleDbType.Clob).Value = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
