using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed class ConnectionManager : IDisposable
{
    private sealed class ActiveConn<TConn> where TConn : class, IDisposable
    {
        public required ConnectionDefinition Definition { get; init; }
        public required TConn Connection { get; init; }
        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveConn<SqlConnection>> _activeSql = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActiveConn<OracleConnection>> _activeOracle = new(StringComparer.OrdinalIgnoreCase);

    private readonly ISecretProtector _protector;
    private readonly IAppLogger _logger;

    public int MaxActiveSql { get; set; } = 2;
    public int MaxActiveOracle { get; set; } = 2;

    public ConnectionManager(ISecretProtector protector, IAppLogger logger)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConnected(ConnectionDefinition def)
    {
        lock (_gate)
        {
            return def.Engine == DatabaseEngine.SqlServer
                ? _activeSql.ContainsKey(def.Name)
                : _activeOracle.ContainsKey(def.Name);
        }
    }

    public IReadOnlyList<ConnectionDefinition> GetConnected(DatabaseEngine engine)
    {
        lock (_gate)
        {
            if (engine == DatabaseEngine.SqlServer)
                return _activeSql.Values.Select(v => v.Definition).ToList();

            return _activeOracle.Values.Select(v => v.Definition).ToList();
        }
    }

    public async Task<(bool ok, string message)> TestAsync(ConnectionDefinition def, CancellationToken cancellationToken)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        def.ValidateForTest();

        try
        {
            if (def.Engine == DatabaseEngine.SqlServer)
            {
                await using var conn = ConnectionStringBuilders.CreateOpenSqlConnection(def);
                await using var cmd = new SqlCommand("SELECT 1", conn);
                _ = await cmd.ExecuteScalarAsync(cancellationToken);
            }
            else
            {
                await using var conn = ConnectionStringBuilders.CreateOpenOracleConnection(def);
                await using var cmd = new OracleCommand("SELECT 1 FROM dual", conn);
                _ = await cmd.ExecuteScalarAsync(cancellationToken);
            }

            return (true, "Connection test succeeded.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Connection test failed for '{def.Name}'.", ex);
            return (false, $"Connection test failed: {ex.Message}");
        }
    }

    public async Task<(bool ok, string message)> ConnectAsync(ConnectionDefinition def, CancellationToken cancellationToken)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        def.ValidateForTest();

        try
        {
            if (def.Engine == DatabaseEngine.SqlServer)
            {
                EnsureCapacitySql(def.Name);
                var conn = ConnectionStringBuilders.CreateOpenSqlConnection(def);
                lock (_gate)
                {
                    _activeSql[def.Name] = new ActiveConn<SqlConnection> { Definition = def, Connection = conn };
                }

                _logger.Info($"Connected SQL Server '{def.Name}'.");
            }
            else
            {
                EnsureCapacityOracle(def.Name);
                var conn = ConnectionStringBuilders.CreateOpenOracleConnection(def);
                lock (_gate)
                {
                    _activeOracle[def.Name] = new ActiveConn<OracleConnection> { Definition = def, Connection = conn };
                }

                _logger.Info($"Connected Oracle '{def.Name}'.");
            }

            await Task.Yield();
            return (true, "Connected.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Connect failed for '{def.Name}'.", ex);
            return (false, $"Connect failed: {ex.Message}");
        }
    }

    public SqlConnection? TryGetOpenSql(string connectionName)
    {
        lock (_gate)
        {
            if (_activeSql.TryGetValue(connectionName, out var a))
            {
                a.LastUsedUtc = DateTimeOffset.UtcNow;
                return a.Connection;
            }
            return null;
        }
    }

    public OracleConnection? TryGetOpenOracle(string connectionName)
    {
        lock (_gate)
        {
            if (_activeOracle.TryGetValue(connectionName, out var a))
            {
                a.LastUsedUtc = DateTimeOffset.UtcNow;
                return a.Connection;
            }
            return null;
        }
    }

    public void Disconnect(ConnectionDefinition def)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));

        lock (_gate)
        {
            if (def.Engine == DatabaseEngine.SqlServer)
            {
                if (_activeSql.Remove(def.Name, out var a))
                {
                    a.Connection.Dispose();
                    _logger.Info($"Disconnected SQL Server '{def.Name}'.");
                }
            }
            else
            {
                if (_activeOracle.Remove(def.Name, out var a))
                {
                    a.Connection.Dispose();
                    _logger.Info($"Disconnected Oracle '{def.Name}'.");
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var a in _activeSql.Values) a.Connection.Dispose();
            foreach (var a in _activeOracle.Values) a.Connection.Dispose();
            _activeSql.Clear();
            _activeOracle.Clear();
        }
    }

    private void EnsureCapacitySql(string connectingName)
    {
        lock (_gate)
        {
            if (_activeSql.Count < MaxActiveSql) return;

            var evict = _activeSql.Values
                .Where(v => !string.Equals(v.Definition.Name, connectingName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.LastUsedUtc)
                .FirstOrDefault();

            if (evict is null) return;
            _activeSql.Remove(evict.Definition.Name);
            evict.Connection.Dispose();
            _logger.Warn($"SQL Server active connection limit reached; evicted least-recently-used connection '{evict.Definition.Name}'.");
        }
    }

    private void EnsureCapacityOracle(string connectingName)
    {
        lock (_gate)
        {
            if (_activeOracle.Count < MaxActiveOracle) return;

            var evict = _activeOracle.Values
                .Where(v => !string.Equals(v.Definition.Name, connectingName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.LastUsedUtc)
                .FirstOrDefault();

            if (evict is null) return;
            _activeOracle.Remove(evict.Definition.Name);
            evict.Connection.Dispose();
            _logger.Warn($"Oracle active connection limit reached; evicted least-recently-used connection '{evict.Definition.Name}'.");
        }
    }
}
