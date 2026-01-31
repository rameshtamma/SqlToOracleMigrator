using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    private async Task EnsureAndSwitchToPdbAsync(OracleConnection openOra, string pdbName, string adminPassword, bool dropIfExists, CancellationToken ct)
    {
        await OraclePdbProvisioner.EnsureAndSwitchToPdbAsync(openOra, pdbName, adminPassword, dropIfExists, ct);
    }
}
