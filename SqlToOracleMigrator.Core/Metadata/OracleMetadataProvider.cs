using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core.Scoring;

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

    // ----------------------------
    // Inventory: object drill-down (paged)
    // ----------------------------
    public async Task<(IReadOnlyList<InventoryObjectSummary> items, bool hasMore)> ListObjectsPagedAsync(
        OracleConnection openConnection,
        int offset,
        int fetch,
        CancellationToken cancellationToken)
    {
        if (openConnection is null) throw new ArgumentNullException(nameof(openConnection));
        fetch = Math.Clamp(fetch, 1, 5000);
        offset = Math.Max(0, offset);

        // Determine the current schema once (do not rely on connection properties; ODP.NET doesn't expose UserId).
        var schema = await GetCurrentSchemaNameAsync(openConnection, cancellationToken);
        if (string.IsNullOrWhiteSpace(schema)) schema = "(current)";

        // Fetch +1 to detect hasMore.
        var effectiveFetch = fetch + 1;

        // Best-effort inventory scoped to the connected user/schema.
        // We avoid DBA views to work in locked-down environments.
        const string sql = @"
SELECT schema_name,
       object_name,
       object_type,
       created_date,
       last_ddl_time,
       est_rows,
       est_size_mb
FROM (
  SELECT
    SYS_CONTEXT('USERENV','CURRENT_SCHEMA') AS schema_name,
    uo.object_name AS object_name,
    uo.object_type AS object_type,
    uo.created     AS created_date,
    uo.last_ddl_time AS last_ddl_time,
    /* rows: only meaningful for tables */
    CASE WHEN uo.object_type = 'TABLE' THEN ut.num_rows ELSE NULL END AS est_rows,
    /* size: segment bytes (TABLE/INDEX/etc). For non-segment objects this will be NULL */
    (SELECT ROUND(SUM(s.bytes) / 1024 / 1024, 3)
       FROM user_segments s
      WHERE s.segment_name = uo.object_name
    ) AS est_size_mb,
    ROW_NUMBER() OVER (ORDER BY uo.object_type, uo.object_name) AS rn
  FROM user_objects uo
  LEFT JOIN user_tables ut
    ON ut.table_name = uo.object_name
  WHERE uo.object_type IN (
    'TABLE','VIEW','PROCEDURE','FUNCTION','SEQUENCE','SYNONYM','TRIGGER','INDEX'
  )
)
WHERE rn > :offset AND rn <= :offset + :fetch
ORDER BY rn";

        var list = new List<InventoryObjectSummary>();

        await using var cmd = new OracleCommand(sql, openConnection);
        cmd.BindByName = true;
        cmd.Parameters.Add(new OracleParameter("offset", offset));
        cmd.Parameters.Add(new OracleParameter("fetch", effectiveFetch));

        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            // schema_name should always come back, but keep a safe fallback.
            var schemaName = rdr.IsDBNull(0) ? schema : rdr.GetString(0);

            var objName = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
            var objTypeRaw = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
            var created = rdr.IsDBNull(3) ? (DateTimeOffset?)null : new DateTimeOffset(rdr.GetDateTime(3));
            var modified = rdr.IsDBNull(4) ? (DateTimeOffset?)null : new DateTimeOffset(rdr.GetDateTime(4));
            long? rows = rdr.IsDBNull(5) ? null : Convert.ToInt64(rdr.GetDecimal(5));
            double? sizeMb = rdr.IsDBNull(6) ? null : Convert.ToDouble(rdr.GetDecimal(6));

            var type = (objTypeRaw ?? string.Empty).Trim().ToUpperInvariant();
            var typeLabel = type switch
            {
                "TABLE" => "Table",
                "VIEW" => "View",
                "PROCEDURE" => "Procedure",
                "FUNCTION" => "Function",
                "SEQUENCE" => "Sequence",
                "SYNONYM" => "Synonym",
                "TRIGGER" => "Trigger",
                "INDEX" => "Index",
                _ => type.Length switch
                {
                    0 => "Other",
                    1 => type,
                    _ => char.ToUpperInvariant(type[0]) + type[1..].ToLowerInvariant()
                }
            };

            list.Add(new InventoryObjectSummary
            {
                Schema = schemaName,
                ObjectName = objName,
                ObjectType = typeLabel,
                EstimatedRows = rows,
                EstimatedSizeMb = sizeMb,
                CreatedDate = created,
                LastModifiedDate = modified
            });
        }

        var hasMore = list.Count > fetch;
        if (hasMore)
            list.RemoveAt(list.Count - 1);

        return (list, hasMore);
    }

    private static async Task<string> GetCurrentSchemaNameAsync(OracleConnection openConnection, CancellationToken cancellationToken)
    {
        try
        {
            // CURRENT_SCHEMA is the most relevant for metadata; USER is session user.
            const string sql = "SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM dual";
            await using var cmd = new OracleCommand(sql, openConnection);
            var obj = await cmd.ExecuteScalarAsync(cancellationToken);
            return obj is null or DBNull ? string.Empty : Convert.ToString(obj) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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

        // Always enforce a usable default tablespace + quota, even when the user already exists.
        // Otherwise inserts can fail later with ORA-01950 (no privileges on tablespace 'SYSTEM').
        var permTs = await GetPreferredPermanentTablespaceAsync(openConnection, cancellationToken);
        var tempTs = await GetPreferredTemporaryTablespaceAsync(openConnection, cancellationToken);

        if (exists)
        {
            await EnsureUserStorageAsync(openConnection, schema, permTs, tempTs, cancellationToken);
            return;
        }

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
        var createSql = $"CREATE USER {userName} IDENTIFIED BY \"{tempPassword}\" DEFAULT TABLESPACE {permTs} TEMPORARY TABLESPACE {tempTs}";
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

        await EnsureUserStorageAsync(openConnection, schema, permTs, tempTs, cancellationToken);

        // Lock/expire user by default? NO — tool expects schema usable for object creation.
        _logger.Info($"[SchemaProvisioning] Created Oracle schema/user {userName}.");
    }

    private async Task<string> GetPreferredPermanentTablespaceAsync(OracleConnection openConnection, CancellationToken ct)
    {
        // Prefer USERS if present, else choose any online permanent tablespace that is not SYSTEM/SYSAUX/UNDO.
        var candidates = new List<string>();
        try
        {
            const string sql = @"
SELECT tablespace_name
FROM   dba_tablespaces
WHERE  contents = 'PERMANENT'
  AND  status   = 'ONLINE'";

            await using var cmd = new OracleCommand(sql, openConnection);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var ts = rdr.GetString(0);
                if (string.IsNullOrWhiteSpace(ts)) continue;
                candidates.Add(ts.Trim().ToUpperInvariant());
            }
        }
        catch
        {
            // If we can't read DBA views (restricted env), fall back to USERS as a best guess.
            return "USERS";
        }

        if (candidates.Contains("USERS")) return "USERS";

        // Pick a safe non-system permanent tablespace if possible.
        foreach (var ts in candidates)
        {
            if (ts is "SYSTEM" or "SYSAUX") continue;
            if (ts.StartsWith("UNDO", StringComparison.OrdinalIgnoreCase)) continue;
            if (ts.StartsWith("TEMP", StringComparison.OrdinalIgnoreCase)) continue;
            return ts;
        }

        // As a last resort (XE minimal installs), use SYSTEM. We'll grant quota on SYSTEM to avoid ORA-01950.
        return "SYSTEM";
    }

    private async Task<string> GetPreferredTemporaryTablespaceAsync(OracleConnection openConnection, CancellationToken ct)
    {
        var candidates = new List<string>();
        try
        {
            const string sql = @"
SELECT tablespace_name
FROM   dba_tablespaces
WHERE  contents = 'TEMPORARY'
  AND  status   = 'ONLINE'";

            await using var cmd = new OracleCommand(sql, openConnection);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var ts = rdr.GetString(0);
                if (string.IsNullOrWhiteSpace(ts)) continue;
                candidates.Add(ts.Trim().ToUpperInvariant());
            }
        }
        catch
        {
            return "TEMP";
        }

        if (candidates.Contains("TEMP")) return "TEMP";
        return candidates.Count > 0 ? candidates[0] : "TEMP";
    }

    private async Task EnsureUserStorageAsync(
        OracleConnection openConnection,
        string schema,
        string permanentTablespace,
        string tempTablespace,
        CancellationToken ct)
    {
        var userName = OracleIdent.FormatSchema(schema);

        // Set default/temp tablespace (best-effort)
        try
        {
            await using var cmd = new OracleCommand(
                $"ALTER USER {userName} DEFAULT TABLESPACE {permanentTablespace} TEMPORARY TABLESPACE {tempTablespace}",
                openConnection);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[SchemaProvisioning] Could not set DEFAULT/TEMP tablespace for {userName}: {ex.Message}");
        }

        // Ensure quota on chosen permanent tablespace (best-effort)
        try
        {
            await using var quotaCmd = new OracleCommand($"ALTER USER {userName} QUOTA UNLIMITED ON {permanentTablespace}", openConnection);
            await quotaCmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[SchemaProvisioning] Could not set quota on {permanentTablespace} tablespace for {userName}: {ex.Message}");
        }
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

    // ----------------------------
    // Inventory: object drill-down (paged)
    // ----------------------------
    }
