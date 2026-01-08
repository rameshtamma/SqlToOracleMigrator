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

        // Do not include Persist Security Info; keep it simple.
        return $"User Id={user};Password={pass};Data Source={dataSource};";
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
