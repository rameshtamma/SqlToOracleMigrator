using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private async Task EnsureAndSwitchToPdbAsync(OracleConnection openOra, string pdbName, string adminPassword, CancellationToken ct)
    {
        OracleMetadataProvider.ValidateOracleIdentifier(pdbName);
        var pdbQuoted = OracleIdent.QuoteIdent(pdbName);

        // Determine current container
        async Task<string?> CurrentContainerAsync()
        {
            await using var cmd = new OracleCommand("SELECT SYS_CONTEXT('USERENV','CON_NAME') FROM DUAL", openOra);
            var v = await cmd.ExecuteScalarAsync(ct);
            return v is null or DBNull ? null : Convert.ToString(v);
        }

        var current = await CurrentContainerAsync();
        if (!string.IsNullOrWhiteSpace(current) && string.Equals(current, pdbName, StringComparison.OrdinalIgnoreCase))
            return;

        // Check if PDB exists (requires access to v$pdbs)
        bool exists;
        await using (var cmd = new OracleCommand("SELECT COUNT(1) FROM v$pdbs WHERE name = :p", openOra))
        {
            cmd.BindByName = true;
            cmd.Parameters.Add(new OracleParameter("p", pdbName.ToUpperInvariant()));
            var val = await cmd.ExecuteScalarAsync(ct);
            exists = Convert.ToInt32(val) > 0;
        }

        if (!exists)
        {
            // Attempt best-effort create (works when OMF is configured or DB_CREATE_FILE_DEST is set)
            var create = $"CREATE PLUGGABLE DATABASE {pdbQuoted} ADMIN USER PDBADMIN IDENTIFIED BY \"{adminPassword}\"";
            await using var createCmd = new OracleCommand(create, openOra);
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        // Open PDB (idempotent)
        try
        {
            await using var openCmd = new OracleCommand($"ALTER PLUGGABLE DATABASE {pdbQuoted} OPEN", openOra);
            await openCmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // ignore
        }

        // Switch session into PDB
        await using (var alter = new OracleCommand($"ALTER SESSION SET CONTAINER = {pdbQuoted}", openOra))
        {
            await alter.ExecuteNonQueryAsync(ct);
        }
    }
}
