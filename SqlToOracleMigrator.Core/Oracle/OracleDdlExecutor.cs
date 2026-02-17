using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace SqlToOracleMigrator.Core.Oracle
{
    /// <summary>
    /// Executes Oracle DDL/DML with resilient, idempotent behavior expected by Schema Build stages.
    /// IMPORTANT: Do NOT use DBMS_SQL.PARSE here (it caused repeated SYS.DBMS_SQL stack frames and brittle failures).
    /// </summary>
    public sealed class OracleDdlExecutor
    {
        private readonly OracleConnection _conn;
        private readonly Serilog.ILogger _log;

        /// <summary>
        /// Back-compat ctor: existing code constructs executor with only an OracleConnection.
        /// </summary>
        public OracleDdlExecutor(OracleConnection conn)
            : this(conn, Log.Logger)
        {
        }

        /// <summary>
        /// Preferred ctor with explicit logger.
        /// </summary>
        public OracleDdlExecutor(OracleConnection conn, Serilog.ILogger log)
        {
            _conn = conn ?? throw new ArgumentNullException(nameof(conn));
            _log = log ?? Log.Logger;
        }

        /// <summary>
        /// Execute a single DDL statement with idempotent ignore list.
        /// </summary>
        public async Task ExecuteIdempotentAsync(string ddl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ddl)) return;

            // Keep it simple: EXECUTE IMMEDIATE only. No DBMS_SQL.
            // Wrap in an anonymous block so we can ignore known idempotent errors safely.
            const string plsql = @"
BEGIN
  EXECUTE IMMEDIATE :p_sql;
EXCEPTION
  WHEN OTHERS THEN
    IF SQLCODE IN (
      -955,   -- ORA-00955 name already used
      -1408,  -- ORA-01408 already indexed
      -2260,  -- ORA-02260 PK already exists
      -2261,  -- ORA-02261 unique constraint already exists
      -1450,  -- ORA-01450 max key length exceeded (skip indexes in Schema Build)
      -2327,  -- ORA-02327 index on LOB expression (XML/LOB)
      -24344  -- ORA-24344 success with compilation error (invalid object)
    ) THEN
      NULL;
    ELSE
      RAISE;
    END IF;
END;";

            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.BindByName = true;
                cmd.CommandType = CommandType.Text;
                cmd.CommandText = plsql;
                cmd.Parameters.Add("p_sql", OracleDbType.Varchar2, ddl, ParameterDirection.Input);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (OracleException ex) when (IsIdempotentNonBlocking(ex))
            {
                _log.Warning(ex, "Non-blocking Oracle error ignored by idempotent executor. Code={Code} DDL={Ddl}",
                    ex.Number, Trunc(ddl, 4000));
                // ignored
            }
        }

        /// <summary>
        /// Execute a non-query statement (DML/DDL). Throws on error.
        /// </summary>
        public async Task ExecuteNonQueryAsync(string sql, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;
            using var cmd = _conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandType = CommandType.Text;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        private static bool IsIdempotentNonBlocking(OracleException ex)
        {
            // OracleException.Number is positive ORA-xxxxx without sign.
            // We map to known cases seen in DeploymentSkeleton.
            return ex.Number is 955 or 1408 or 2260 or 2261 or 1450 or 2327 or 24344;
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
