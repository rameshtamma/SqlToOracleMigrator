using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed class OracleMetadataProvider
{
    private readonly IAppLogger _logger;

    public OracleMetadataProvider(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<InventoryDbSummary> GetServiceSummaryAsync(OracleConnection openConnection, string serviceLabel, string defaultUser, CancellationToken cancellationToken)
    {
        // Best-effort; Oracle metadata access varies by privilege.
        var summary = new InventoryDbSummary
        {
            Side = "Target",
            Engine = "Oracle",
            DatabaseOrService = serviceLabel,
            DefaultSchemaOrUser = defaultUser
        };

        try
        {
            const string sql = @"SELECT 
  (SELECT COUNT(*) FROM user_tables) AS table_count,
  (SELECT COUNT(*) FROM user_views) AS view_count,
  (SELECT COUNT(*) FROM user_objects WHERE object_type='PROCEDURE') AS proc_count,
  (SELECT COUNT(*) FROM user_objects WHERE object_type='FUNCTION') AS func_count,
  (SELECT COUNT(*) FROM user_sequences) AS seq_count,
  (SELECT COUNT(*) FROM user_synonyms) AS syn_count,
  (SELECT COUNT(*) FROM user_triggers) AS trg_count
FROM dual";

            await using var cmd = new OracleCommand(sql, openConnection);
            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await rdr.ReadAsync(cancellationToken))
            {
                summary.TableCount = rdr.IsDBNull(0) ? null : rdr.GetInt32(0);
                summary.ViewCount = rdr.IsDBNull(1) ? null : rdr.GetInt32(1);
                summary.ProcedureCount = rdr.IsDBNull(2) ? null : rdr.GetInt32(2);
                summary.FunctionCount = rdr.IsDBNull(3) ? null : rdr.GetInt32(3);
                summary.SequenceCount = rdr.IsDBNull(4) ? null : rdr.GetInt32(4);
                summary.SynonymCount = rdr.IsDBNull(5) ? null : rdr.GetInt32(5);
                summary.TriggerCount = rdr.IsDBNull(6) ? null : rdr.GetInt32(6);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Oracle summary query failed (insufficient privileges or unsupported views): {ex.Message}");
        }

        return summary;
    }

    
    private static async Task<string?> GetContainerNameAsync(OracleConnection openConnection, CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = new OracleCommand("SELECT SYS_CONTEXT('USERENV','CON_NAME') FROM dual", openConnection);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result?.ToString();
        }
        catch
        {
            return null; // best-effort
        }
    }

public static void ValidateOracleIdentifier(string ident)
    {
        if (string.IsNullOrWhiteSpace(ident))
            throw new InvalidOperationException("Oracle schema/user is required.");

        // Basic Oracle identifier rule (unquoted): starts with letter, then letters/digits/_$#
        // We'll allow quoted usage via QuoteIdent, but still reject whitespace/control.
        if (ident.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Oracle schema/user cannot contain spaces.");
    }

    public async Task EnsureSchemaExistsAsync(OracleConnection openConnection, string schema, CancellationToken cancellationToken)
    {
        // Usually schemas == users. We won't auto-create users; only validate existence.
        const string sql = "SELECT COUNT(*) FROM all_users WHERE username = :u";
        await using var cmd = new OracleCommand(sql, openConnection);
        cmd.Parameters.Add(new OracleParameter("u", OracleIdent.NormalizeSchemaForLookup(schema)));
        var countObj = await cmd.ExecuteScalarAsync(cancellationToken);
        var count = countObj is null or DBNull ? 0 : Convert.ToInt32(countObj);
        if (count <= 0)
            throw new InvalidOperationException(
                $"Target schema/user '{schema}' does not exist. " +
                "Oracle schemas are users; create the user/schema in Oracle or choose an existing one.");
    }

    // ----------------------------
    // v6: schema provisioning + validation helpers
    // ----------------------------

    public async Task<bool> SchemaExistsAsync(OracleConnection openConnection, string schema, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM all_users WHERE username = :u";
        await using var cmd = new OracleCommand(sql, openConnection);
        cmd.Parameters.Add(new OracleParameter("u", OracleIdent.NormalizeSchemaForLookup(schema)));
        var countObj = await cmd.ExecuteScalarAsync(cancellationToken);
        var count = countObj is null or DBNull ? 0 : Convert.ToInt32(countObj);
        return count > 0;
    }

    /// <summary>
    /// Ensures a schema/user exists. If autoCreate=true, attempts to CREATE USER and grant basic privileges.
    /// This requires admin privileges (e.g., SYSTEM) and may vary by environment.
    /// </summary>
    public async Task EnsureSchemaUserExistsAsync(
        OracleConnection openConnection,
        string schema,
        bool autoCreate,
        CancellationToken cancellationToken)
    {
        ValidateOracleIdentifier(schema);
        var normLookup = OracleIdent.NormalizeSchemaForLookup(schema);
        var exists = await SchemaExistsAsync(openConnection, schema, cancellationToken);
        if (exists) return;

        if (!autoCreate)
            throw new InvalidOperationException($"Target schema/user '{schema}' does not exist.");

        var userName = OracleIdent.FormatSchema(schema);

                // CDB/PDB guard: creating non-common users in CDB$ROOT triggers ORA-65096 unless name is prefixed with C##.
        // This tool requires exact schema names (no C## prefix), so we enforce PDB-only user/schema provisioning.
        var conName = await GetContainerNameAsync(openConnection, cancellationToken);
        if (string.Equals(conName, "CDB$ROOT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Connected to Oracle container '{conName}'. Creating local schema/users with exact names is not allowed here (ORA-65096). " +
                "Please connect using a PDB/service name (pluggable database) and retry, or pre-create the target schemas/users in the PDB.");
        }

// Generate a one-time random password (the app does not log the actual password).
        var tempPassword = "Tmp_" + Guid.NewGuid().ToString("N")[..20];

        _logger.Info($"[SchemaProvisioning] Creating Oracle user/schema {userName} (temporary password generated).");

        // Note: Do NOT quote usernames unless necessary; quoted identifiers become case-sensitive.
        var createSql = $"CREATE USER {userName} IDENTIFIED BY \"{tempPassword}\"";
        await using (var createCmd = new OracleCommand(createSql, openConnection))
        {
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Basic grants; may be adjusted per environment policies.
        var grants = new[]
        {
            $"GRANT CREATE SESSION TO {userName}",
            $"GRANT CREATE TABLE TO {userName}",
            $"GRANT CREATE VIEW TO {userName}",
            $"GRANT CREATE SEQUENCE TO {userName}",
            $"GRANT CREATE PROCEDURE TO {userName}",
            $"GRANT CREATE TRIGGER TO {userName}"
        };

        foreach (var g in grants)
        {
            try
            {
                await using var grantCmd = new OracleCommand(g, openConnection);
                await grantCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[SchemaProvisioning] Grant failed: {g}. {ex.Message}");
            }
        }

        // Try to set an unlimited quota (best-effort). The tablespace name varies; USERS is common.
        try
        {
            await using var quotaCmd = new OracleCommand($"ALTER USER {userName} QUOTA UNLIMITED ON USERS", openConnection);
            await quotaCmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[SchemaProvisioning] Could not set quota on USERS tablespace for {userName}: {ex.Message}");
        }

        // Lock/expire user by default? NO — tool expects schema usable for object creation.
        _logger.Info($"[SchemaProvisioning] Created Oracle schema/user {userName}.");
    }

    /// <summary>
    /// Parse-only DDL validation using DBMS_SQL.PARSE. This does not create objects.
    /// </summary>
    public async Task ValidateDdlAsync(OracleConnection openConnection, string ddl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ddl)) return;

        const string plsql = @"
DECLARE
  c INTEGER;
BEGIN
  c := DBMS_SQL.OPEN_CURSOR;
  DBMS_SQL.PARSE(c, :ddl_text, DBMS_SQL.NATIVE);
  DBMS_SQL.CLOSE_CURSOR(c);
EXCEPTION
  WHEN OTHERS THEN
    BEGIN
      IF DBMS_SQL.IS_OPEN(c) THEN
        DBMS_SQL.CLOSE_CURSOR(c);
      END IF;
    EXCEPTION WHEN OTHERS THEN NULL;
    END;
    RAISE;
END;";

        await using var cmd = new OracleCommand(plsql, openConnection);
        cmd.BindByName = true;
        cmd.Parameters.Add(new OracleParameter("ddl_text", ddl));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

}
