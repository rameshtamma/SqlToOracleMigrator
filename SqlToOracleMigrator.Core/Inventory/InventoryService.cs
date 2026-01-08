using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core;

public sealed class InventoryService
{
    private readonly ConnectionManager _connMgr;
    private readonly SqlServerMetadataProvider _sqlMeta;
    private readonly OracleMetadataProvider _oraMeta;
    private readonly IAppLogger _logger;

    public InventoryService(ConnectionManager connMgr, SqlServerMetadataProvider sqlMeta, OracleMetadataProvider oraMeta, IAppLogger logger)
    {
        _connMgr = connMgr ?? throw new ArgumentNullException(nameof(connMgr));
        _sqlMeta = sqlMeta ?? throw new ArgumentNullException(nameof(sqlMeta));
        _oraMeta = oraMeta ?? throw new ArgumentNullException(nameof(oraMeta));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<InventoryDbSummary>> LoadSqlInventoryAsync(ConnectionDefinition sqlConnection, CancellationToken cancellationToken)
    {
        if (sqlConnection.Engine != DatabaseEngine.SqlServer)
            throw new InvalidOperationException("Expected SQL Server connection.");

        var open = _connMgr.TryGetOpenSql(sqlConnection.Name)
            ?? throw new InvalidOperationException("SQL Server connection is not connected.");

        var dbs = await _sqlMeta.ListDatabasesAsync(open, cancellationToken);
        var list = new List<InventoryDbSummary>();

        foreach (var db in dbs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var summary = await _sqlMeta.GetDbSummaryAsync(open, db, cancellationToken);
                list.Add(summary);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to load summary for SQL DB '{db}': {ex.Message}");
                list.Add(new InventoryDbSummary
                {
                    Side = "Source",
                    Engine = "SQL Server",
                    DatabaseOrService = db,
                    DefaultSchemaOrUser = "dbo"
                });
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<InventoryDbSummary>> LoadOracleInventoryAsync(ConnectionDefinition oracleConnection, CancellationToken cancellationToken)
    {
        if (oracleConnection.Engine != DatabaseEngine.Oracle)
            throw new InvalidOperationException("Expected Oracle connection.");

        var open = _connMgr.TryGetOpenOracle(oracleConnection.Name)
            ?? throw new InvalidOperationException("Oracle connection is not connected.");

        var serviceLabel = oracleConnection.UseSid ? oracleConnection.Sid ?? oracleConnection.Name : oracleConnection.ServiceName ?? oracleConnection.Name;
        var summary = await _oraMeta.GetServiceSummaryAsync(open, serviceLabel, oracleConnection.Username ?? "", cancellationToken);
        return new List<InventoryDbSummary> { summary };
    }

    public async Task<(IReadOnlyList<InventoryObjectSummary> items, bool hasMore)> LoadSqlObjectsAsync(
        ConnectionDefinition sqlConnection,
        string database,
        int offset,
        int fetch,
        CancellationToken cancellationToken)
    {
        var open = _connMgr.TryGetOpenSql(sqlConnection.Name)
            ?? throw new InvalidOperationException("SQL Server connection is not connected.");
        return await _sqlMeta.ListObjectsPagedAsync(open, database, offset, fetch, cancellationToken);
    }
}
