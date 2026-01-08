using System.IO;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Core.Tracking;

namespace SqlToOracleMigrator.Desktop.Services;

public sealed class AppBootstrapper : IDisposable
{
    private AppServices? _services;

    public void Initialize()
    {
        var paths = new AppPaths();
        paths.EnsureCreated();

        var protector = new DpapiSecretProtector();
        var store = new JsonConnectionStore(paths);

        var queryStore = new JsonSqlQueryStore(Path.Combine(paths.ConfigDirectory, "sqlqueries.json"));
        var mappingConfig = DataTypeMappingConfig.Load(Path.Combine(paths.ConfigDirectory, "datatype_mappings.json"));

        var fileLogger = new FileAppLogger(paths);
        var memLogger = new InMemoryAppLogger();
        var logger = new CompositeLogger(fileLogger, memLogger);

        var connMgr = new ConnectionManager(protector, logger);
        // load limits from appsettings.json (best-effort)
        TryApplyLimits(paths, connMgr, logger);

        var sqlMeta = new SqlServerMetadataProvider(queryStore, logger);
        var oraMeta = new OracleMetadataProvider(logger);
        var inv = new InventoryService(connMgr, sqlMeta, oraMeta, logger);

        var mapper = new SqlToOracleTypeMapper(mappingConfig);
        var toolMig = new ToolMigRepository(logger);
        var engine = new MigrationEngine(connMgr, sqlMeta, oraMeta, mapper, queryStore, logger, paths, toolMig);

        var authTypes = new JsonListStore(Path.Combine(paths.ConfigDirectory, "auth_types.json")).LoadStrings();
        var connTypes = new JsonListStore(Path.Combine(paths.ConfigDirectory, "connection_types.json")).LoadStrings();

        _services = new AppServices(paths, protector, store, queryStore, mappingConfig, logger, connMgr, sqlMeta, oraMeta, inv, engine, toolMig, authTypes, connTypes);
    }

    public void Dispose()
    {
        _services?.Dispose();
        _services = null;
    }

    private static void TryApplyLimits(AppPaths paths, ConnectionManager connMgr, IAppLogger logger)
    {
        try
        {
            var file = Path.Combine(paths.ConfigDirectory, "appsettings.json");
            if (!File.Exists(file)) return;

            var json = File.ReadAllText(file);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("limits", out var limits))
            {
                if (limits.TryGetProperty("maxActiveSqlConnections", out var v1) && v1.TryGetInt32(out var maxSql))
                    connMgr.MaxActiveSql = Math.Max(1, maxSql);

                if (limits.TryGetProperty("maxActiveOracleConnections", out var v2) && v2.TryGetInt32(out var maxOra))
                    connMgr.MaxActiveOracle = Math.Max(1, maxOra);
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to load appsettings.json limits: {ex.Message}");
        }
    }
}
