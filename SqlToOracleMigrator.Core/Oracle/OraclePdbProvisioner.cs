using System;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

/// <summary>
/// Creates/opens/switches an Oracle pluggable database (PDB) from a CDB root connection.
/// This logic is shared between the migration runner and UI utilities.
/// </summary>
public static class OraclePdbProvisioner
{
    public static async Task EnsureAndSwitchToPdbAsync(
        OracleConnection openOra,
        string pdbName,
        string adminPassword,
        bool dropIfExists,
        CancellationToken ct)
    {
        OracleMetadataProvider.ValidateOracleIdentifier(pdbName);
        var pdbQuoted = OracleIdent.QuoteIdent(pdbName);

        var current = await OraclePdbAdmin.GetCurrentContainerAsync(openOra, ct);
        if (!string.IsNullOrWhiteSpace(current) && string.Equals(current, pdbName, StringComparison.OrdinalIgnoreCase))
            return;

        // For create/drop operations we must be in CDB$ROOT.
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, "CDB$ROOT", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await using var root = new OracleCommand("ALTER SESSION SET CONTAINER = CDB$ROOT", openOra);
                await root.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                // ignore; subsequent operations will fail with clearer message
            }
        }

        var containerAfter = await OraclePdbAdmin.GetCurrentContainerAsync(openOra, ct);
        if (!string.Equals(containerAfter, "CDB$ROOT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot manage PDBs because the Oracle session is connected to container '{containerAfter}'. " +
                "To create/drop/switch PDBs, connect to the CDB root service (e.g., XE) using an account with SYSDBA privileges.");
        }

        bool exists;
        try
        {
            await using var cmd = new OracleCommand("SELECT COUNT(1) FROM v$pdbs WHERE name = :p", openOra);
            cmd.BindByName = true;
            cmd.Parameters.Add(new OracleParameter("p", pdbName.ToUpperInvariant()));
            var val = await cmd.ExecuteScalarAsync(ct);
            exists = Convert.ToInt32(val) > 0;
        }
        catch (OracleException ex)
        {
            throw new InvalidOperationException(
                "Unable to query v$pdbs to determine whether the target PDB exists. " +
                "This usually means the selected Oracle connection lacks catalog privileges. " +
                "Use SYSDBA (recommended) or grant SELECT on V_$PDBS.", ex);
        }

        if (exists && dropIfExists)
        {
            await OraclePdbAdmin.DropPdbAsync(openOra, pdbName, includingDatafiles: true, ct);
            exists = false;
        }

        if (!exists)
        {
            string? dbCreateFileDest = null;
            try
            {
                await using var p = new OracleCommand("SELECT value FROM v$parameter WHERE name = 'db_create_file_dest'", openOra);
                var v = await p.ExecuteScalarAsync(ct);
                dbCreateFileDest = v is null or DBNull ? null : Convert.ToString(v);
                if (string.IsNullOrWhiteSpace(dbCreateFileDest)) dbCreateFileDest = null;
            }
            catch
            {
                // ignore
            }

            string create;
            if (dbCreateFileDest is not null)
            {
                create = $"CREATE PLUGGABLE DATABASE {pdbQuoted} ADMIN USER PDBADMIN IDENTIFIED BY \"{adminPassword}\"";
            }
            else
            {
                string? seedFile = null;
                int seedConId = 2;

                try
                {
                    await using var seedIdCmd = new OracleCommand("SELECT con_id FROM v$pdbs WHERE name = 'PDB$SEED'", openOra);
                    var v = await seedIdCmd.ExecuteScalarAsync(ct);
                    if (v is not null && v is not DBNull)
                        seedConId = Convert.ToInt32(v);
                }
                catch
                {
                    // ignore
                }

                try
                {
                    await using var seedCmd = new OracleCommand("SELECT file_name FROM cdb_data_files WHERE con_id = :cid AND ROWNUM = 1", openOra);
                    seedCmd.BindByName = true;
                    seedCmd.Parameters.Add(new OracleParameter("cid", seedConId));
                    var v = await seedCmd.ExecuteScalarAsync(ct);
                    seedFile = v is null or DBNull ? null : Convert.ToString(v);
                }
                catch
                {
                    // ignore
                }

                if (string.IsNullOrWhiteSpace(seedFile))
                {
                    try
                    {
                        await using var seedCmd2 = new OracleCommand("SELECT name FROM v$datafile WHERE con_id = :cid AND ROWNUM = 1", openOra);
                        seedCmd2.BindByName = true;
                        seedCmd2.Parameters.Add(new OracleParameter("cid", seedConId));
                        var v = await seedCmd2.ExecuteScalarAsync(ct);
                        seedFile = v is null or DBNull ? null : Convert.ToString(v);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (string.IsNullOrWhiteSpace(seedFile))
                {
                    try
                    {
                        await using var toSeed = new OracleCommand("ALTER SESSION SET CONTAINER = PDB$SEED", openOra);
                        await toSeed.ExecuteNonQueryAsync(ct);

                        await using var seedCmd3 = new OracleCommand("SELECT file_name FROM dba_data_files WHERE ROWNUM = 1", openOra);
                        var v = await seedCmd3.ExecuteScalarAsync(ct);
                        seedFile = v is null or DBNull ? null : Convert.ToString(v);

                        await using var backRoot = new OracleCommand("ALTER SESSION SET CONTAINER = CDB$ROOT", openOra);
                        await backRoot.ExecuteNonQueryAsync(ct);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (string.IsNullOrWhiteSpace(seedFile))
                {
                    throw new InvalidOperationException(
                        "Cannot create PDB because seed datafile location could not be determined. " +
                        "This typically means the connected Oracle user cannot see PDB$SEED rows in V$PDBS/CDB_DATA_FILES (or lacks V$ access). " +
                        "Fix options: (1) connect as SYSDBA (recommended), (2) grant SELECT on V_$PDBS and CDB_DATA_FILES (or V_$DATAFILE), " +
                        "or (3) set DB_CREATE_FILE_DEST so Oracle can place files automatically.");
                }

                var lastSlash = Math.Max(seedFile!.LastIndexOf('/'), seedFile.LastIndexOf('\\'));
                var seedDir = lastSlash >= 0 ? seedFile[..(lastSlash + 1)] : seedFile;
                var targetDir = seedDir;

                var idx = seedDir.IndexOf("pdbseed", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    targetDir = seedDir[..idx] + pdbName + seedDir[(idx + "pdbseed".Length)..];
                }
                else
                {
                    targetDir = seedDir.TrimEnd('/', '\\') + (seedDir.Contains('\\') ? "\\" : "/") + pdbName + (seedDir.Contains('\\') ? "\\" : "/");
                }

                var seedDirLit = seedDir.Replace("'", "''");
                var targetDirLit = targetDir.Replace("'", "''");
                create = $"CREATE PLUGGABLE DATABASE {pdbQuoted} ADMIN USER PDBADMIN IDENTIFIED BY \"{adminPassword}\" FILE_NAME_CONVERT = ('{seedDirLit}','{targetDirLit}')";
            }

            await using var createCmd = new OracleCommand(create, openOra);
            try
            {
                await createCmd.ExecuteNonQueryAsync(ct);
            }
            catch (OracleException ex) when (ex.Number == 65016)
            {
                throw new InvalidOperationException(
                    "Oracle requires FILE_NAME_CONVERT (or DB_CREATE_FILE_DEST) to create a new PDB in this environment. " +
                    "Ensure the target directory exists and/or set DB_CREATE_FILE_DEST.", ex);
            }
        }

        try
        {
            await using var openCmd = new OracleCommand($"ALTER PLUGGABLE DATABASE {pdbQuoted} OPEN READ WRITE", openOra);
            await openCmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // ignore
        }

        try
        {
            await using var save = new OracleCommand($"ALTER PLUGGABLE DATABASE {pdbQuoted} SAVE STATE", openOra);
            await save.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // ignore
        }

        await using (var alter = new OracleCommand($"ALTER SESSION SET CONTAINER = {pdbQuoted}", openOra))
        {
            await alter.ExecuteNonQueryAsync(ct);
        }
    }
}
