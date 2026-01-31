using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public static class ConnectionStringBuilders
{
    public static string BuildSqlServer(ConnectionDefinition def, string? passwordOverride = null)
    {
        if (def.Engine != DatabaseEngine.SqlServer)
            throw new InvalidOperationException("Not a SQL Server connection definition.");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{def.Hostname},{def.Port}",
            InitialCatalog = string.IsNullOrWhiteSpace(def.DefaultDatabase) ? "master" : def.DefaultDatabase,
            TrustServerCertificate = true,
            ConnectTimeout = 10,
            Encrypt = false,
            MultipleActiveResultSets = true
        };

        if (def.UseWindowsAuthentication)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = def.Username ?? "";
            builder.Password = passwordOverride ?? def.RuntimePassword ?? "";
        }

        return builder.ConnectionString;
    }

    public static string BuildOracle(ConnectionDefinition def, string? passwordOverride = null)
    {
        if (def.Engine != DatabaseEngine.Oracle)
            throw new InvalidOperationException("Not an Oracle connection definition.");

        var user = def.Username ?? "";
        var pass = passwordOverride ?? def.RuntimePassword ?? "";

        var host = def.Hostname;
        var port = def.Port;
        var connectData = def.UseSid
            ? $"(SID={def.Sid})"
            : $"(SERVICE_NAME={def.ServiceName})";

        var dataSource = $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))(CONNECT_DATA={connectData}))";

        // Enable SYSDBA when requested (needed for CDB/PDB provisioning).
        var role = (def.Role ?? string.Empty).Trim();
        var dbaPriv = string.Empty;
        if (string.Equals(user, "SYS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "SYSDBA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "SYSOPER", StringComparison.OrdinalIgnoreCase))
        {
            // ODP.NET connection string key
            dbaPriv = "DBA Privilege=SYSDBA;";
        }

        // Connection resiliency defaults:
        // - Pooling enabled for performance, with Validate Connection to avoid stale pooled sessions.
        // - Higher connect/pool request timeout to reduce first-click failures.
        // Note: pool request timeouts can surface as ORA-50000 from ODP.NET.
        var extras = "Pooling=true;Validate Connection=true;Connection Timeout=30;Min Pool Size=0;Max Pool Size=20;Incr Pool Size=2;Decr Pool Size=1;";

        // Do not include Persist Security Info; keep it simple.
        return $"User Id={user};Password={pass};{dbaPriv}Data Source={dataSource};{extras}";
    }

    public static SqlConnection CreateOpenSqlConnection(ConnectionDefinition def, string? passwordOverride = null)
    {
        var cs = BuildSqlServer(def, passwordOverride);
        var conn = new SqlConnection(cs);
        conn.Open();
        return conn;
    }

    public static OracleConnection CreateOpenOracleConnection(ConnectionDefinition def, string? passwordOverride = null)
    {
        var cs = BuildOracle(def, passwordOverride);
        var conn = new OracleConnection(cs);
        conn.Open();
        return conn;
    }
}
