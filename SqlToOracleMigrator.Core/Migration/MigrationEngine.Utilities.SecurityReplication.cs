using Oracle.ManagedDataAccess.Client;
using System.Text.Json;

namespace SqlToOracleMigrator.Core;

public sealed partial class MigrationEngine
{
    internal sealed record SecurityRoleGrant(string Role, string Privilege, string ObjectName, string? ObjectSchema);
    internal sealed record SecurityRoleAssignment(string Role, string Grantee, string GranteeType); // USER or ROLE or EXTERNAL

    private sealed class SecurityModelArtifact
    {
        public List<SecurityRoleGrant> RoleGrants { get; set; } = new();
        public List<SecurityRoleAssignment> RoleAssignments { get; set; } = new();
    }

    private async Task ApplySecurityReplicationAsync(MigrationContext ctx, CancellationToken ct)
    {
        // Roles are always auto-created (default behavior).
        // User grants are strict: fail if expected USERS missing (schema owners + SQL users), but do not fail for AD/external.
        var artifactPath = Path.Combine(ctx.RunDir, "security_model.json");
        if (!File.Exists(artifactPath))
        {
            ctx.AppendLog("[FinalVerification][WARN] security_model.json not found. Skipping security replication.");
            return;
        }

        SecurityModelArtifact? model;
        try
        {
            var json = await File.ReadAllTextAsync(artifactPath, ct);
            model = JsonSerializer.Deserialize<SecurityModelArtifact>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            ctx.AppendLog($"[FinalVerification][ERROR] Failed to read security_model.json: {ex.Message}");
            throw;
        }

        model ??= new SecurityModelArtifact();

        await CreateRolesAndApplyObjectGrantsAsync(ctx, model, ct);
        await ApplyRoleAssignmentsStrictAsync(ctx, model, ct);
    }

    private async Task CreateRolesAndApplyObjectGrantsAsync(MigrationContext ctx, SecurityModelArtifact model, CancellationToken ct)
    {
        if (ctx.Request.SecurityGrantMode != SecurityGrantMode.AutoApplyRolesAndObjectGrants)
            return;

        foreach (var rg in model.RoleGrants)
        {
            ct.ThrowIfCancellationRequested();
            var role = OracleIdent.QuoteIdent(rg.Role);
            var createRole = $"BEGIN EXECUTE IMMEDIATE 'CREATE ROLE {role}'; EXCEPTION WHEN OTHERS THEN NULL; END;";
            await using (var c = new OracleCommand(createRole, ctx.OpenOra) { CommandTimeout = 0 })
                await c.ExecuteNonQueryAsync(ct);

            var obj = rg.ObjectSchema is { Length: > 0 }
                ? $"{OracleIdent.FormatSchema(rg.ObjectSchema)}.{OracleIdent.QuoteIdent(rg.ObjectName)}"
                : OracleIdent.QuoteIdent(rg.ObjectName);

            var grant = $"GRANT {rg.Privilege} ON {obj} TO {role}";
            await using (var g = new OracleCommand(grant, ctx.OpenOra) { CommandTimeout = 0 })
                await g.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task ApplyRoleAssignmentsStrictAsync(MigrationContext ctx, SecurityModelArtifact model, CancellationToken ct)
    {
        var missingUsers = new List<string>();

        foreach (var ra in model.RoleAssignments)
        {
            ct.ThrowIfCancellationRequested();

            var role = OracleIdent.QuoteIdent(ra.Role);
            var grantee = OracleIdent.QuoteIdent(ra.Grantee);

            if (ra.GranteeType.Equals("ROLE", StringComparison.OrdinalIgnoreCase))
            {
                var sql = $"GRANT {role} TO {grantee}";
                await using var cmd = new OracleCommand(sql, ctx.OpenOra) { CommandTimeout = 0 };
                await cmd.ExecuteNonQueryAsync(ct);
                continue;
            }

            if (ra.GranteeType.Equals("USER", StringComparison.OrdinalIgnoreCase))
            {
                var exists = await OracleUserExistsAsync(ctx.OpenOra, ra.Grantee, ct);
                if (!exists)
                {
                    missingUsers.Add(ra.Grantee.ToUpperInvariant());
                    continue;
                }

                var sql = $"GRANT {role} TO {grantee}";
                await using var cmd = new OracleCommand(sql, ctx.OpenOra) { CommandTimeout = 0 };
                await cmd.ExecuteNonQueryAsync(ct);
                continue;
            }

            // EXTERNAL/AD groups/script-only.
            // Do not auto-apply; do not fail if configured.
            continue;
        }

        if (missingUsers.Count > 0 && ctx.Request.StrictFailOnMissingSecurityUsers)
        {
            // Fail-fast Option 1C: only schema owners + SQL users should be in RoleAssignments as USER.
            // AD/external principals should be tagged EXTERNAL and excluded from failure.
            var msg = $"Finalization failed: Missing users required for role grants. Users not found: {string.Join(", ", missingUsers.Distinct())}.";
            ctx.AppendLog("[FinalVerification][ERROR] " + msg);

            // Write a helper script template
            var templatePath = Path.Combine(ctx.RunDir, "missing_users_template.sql");
            await File.WriteAllTextAsync(templatePath,
                "-- Create the following users in the target PDB before resuming:\n" +
                string.Join("\n", missingUsers.Distinct().Select(u => $"-- CREATE USER {u} IDENTIFIED BY \"temp_password\";")), ct);

            throw new InvalidOperationException(msg + " Action required: create these users in the target PDB, then resume.");
        }
    }

    private static async Task<bool> OracleUserExistsAsync(OracleConnection openOra, string username, CancellationToken ct)
    {
        const string sql = "SELECT 1 FROM ALL_USERS WHERE USERNAME = :p_user";
        await using var cmd = new OracleCommand(sql, openOra) { BindByName = true };
        cmd.Parameters.Add(new OracleParameter("p_user", OracleDbType.Varchar2, username.ToUpperInvariant(), System.Data.ParameterDirection.Input));
        var o = await cmd.ExecuteScalarAsync(ct);
        return o != null;
    }
}
