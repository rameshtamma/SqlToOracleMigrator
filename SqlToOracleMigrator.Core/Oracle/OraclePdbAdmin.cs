using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

/// <summary>
/// Administrative helpers for Oracle multitenant (PDB) operations.
/// Intended to be used only with SYSDBA connections.
/// </summary>
public static class OraclePdbAdmin
{
    public sealed record PdbInfo(string Name, string OpenMode, string Restricted);

    public static async Task<string> GetCurrentContainerAsync(OracleConnection openOra, CancellationToken ct)
    {
        await using var cmd = new OracleCommand("SELECT SYS_CONTEXT('USERENV','CON_NAME') FROM DUAL", openOra);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? string.Empty : Convert.ToString(v) ?? string.Empty;
    }

    public static async Task<List<PdbInfo>> ListPdbsAsync(OracleConnection openOra, CancellationToken ct)
    {
        var list = new List<PdbInfo>();

        const string sql = @"SELECT name, open_mode, restricted FROM v$pdbs ORDER BY name";
        await using var cmd = new OracleCommand(sql, openOra);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var name = rdr.GetString(0);
            var openMode = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
            var restricted = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
            list.Add(new PdbInfo(name, openMode, restricted));
        }
        return list;
    }

    public static bool IsProtectedPdbName(string pdbName)
    {
        if (string.IsNullOrWhiteSpace(pdbName)) return true;
        var n = pdbName.Trim();
        if (n.Equals("PDB$SEED", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith("XEPDB", StringComparison.OrdinalIgnoreCase)) return true; // XE default PDB(s)
        return false;
    }

    public static async Task DropPdbAsync(OracleConnection openOra, string pdbName, bool includingDatafiles, CancellationToken ct)
    {
        OracleMetadataProvider.ValidateOracleIdentifier(pdbName);
        if (IsProtectedPdbName(pdbName))
            throw new InvalidOperationException($"Refusing to drop protected PDB '{pdbName}'.");

        // Always operate from CDB$ROOT.
        await using (var root = new OracleCommand("ALTER SESSION SET CONTAINER = CDB$ROOT", openOra))
            await root.ExecuteNonQueryAsync(ct);

        // Close (idempotent)
        try
        {
            await using var close = new OracleCommand($"ALTER PLUGGABLE DATABASE {OracleIdent.QuoteIdent(pdbName)} CLOSE IMMEDIATE", openOra);
            await close.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // ignore
        }

        // Drop
        var dropSql = includingDatafiles
            ? $"DROP PLUGGABLE DATABASE {OracleIdent.QuoteIdent(pdbName)} INCLUDING DATAFILES"
            : $"DROP PLUGGABLE DATABASE {OracleIdent.QuoteIdent(pdbName)}";

        await using var drop = new OracleCommand(dropSql, openOra);
        await drop.ExecuteNonQueryAsync(ct);
    }
}
