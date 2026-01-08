using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Core.Tracking;
using SqlToOracleMigrator.Desktop.ViewModels;

namespace SqlToOracleMigrator.Desktop.Services;

public sealed class AppServices : IDisposable
{
    public static AppServices? Current { get; private set; }

    public AppPaths Paths { get; }
    public ISecretProtector Protector { get; }
    public JsonConnectionStore ConnectionStore { get; }
    public ISqlQueryStore QueryStore { get; }
    public DataTypeMappingConfig TypeMappings { get; }
    public IAppLogger Logger { get; }
    public ConnectionManager ConnectionManager { get; }
    public SqlServerMetadataProvider SqlMetadata { get; }
    public OracleMetadataProvider OracleMetadata { get; }
    public InventoryService InventoryService { get; }
    public MigrationEngine MigrationEngine { get; }
    public ToolMigRepository ToolMigRepository { get; }

    public MainViewModel MainViewModel { get; }

    public IReadOnlyList<string> AuthTypes { get; }
    public IReadOnlyList<string> ConnectionTypes { get; }

    public AppServices(
        AppPaths paths,
        ISecretProtector protector,
        JsonConnectionStore connectionStore,
        ISqlQueryStore queryStore,
        DataTypeMappingConfig mappings,
        IAppLogger logger,
        ConnectionManager connectionManager,
        SqlServerMetadataProvider sqlMetadata,
        OracleMetadataProvider oracleMetadata,
        InventoryService inventoryService,
        MigrationEngine migrationEngine,
        ToolMigRepository toolMigRepository,
        IReadOnlyList<string> authTypes,
        IReadOnlyList<string> connectionTypes)
    {
        Paths = paths;
        Protector = protector;
        ConnectionStore = connectionStore;
        QueryStore = queryStore;
        TypeMappings = mappings;
        Logger = logger;
        ConnectionManager = connectionManager;
        SqlMetadata = sqlMetadata;
        OracleMetadata = oracleMetadata;
        InventoryService = inventoryService;
        MigrationEngine = migrationEngine;
        ToolMigRepository = toolMigRepository;
        AuthTypes = authTypes;
        ConnectionTypes = connectionTypes;

        MainViewModel = new MainViewModel();
        MainViewModel.Initialize(this);

        Current = this;
    }

    public void Dispose()
    {
        try
        {
            (Logger as IDisposable)?.Dispose();
        }
        catch { }

        try
        {
            ConnectionManager.Dispose();
        }
        catch { }
    }
}
