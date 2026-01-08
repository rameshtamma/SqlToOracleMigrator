using System.Collections.ObjectModel;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Desktop.Services;

namespace SqlToOracleMigrator.Desktop.ViewModels;

public sealed class InventoryDbRowViewModel : NotifyBase
{
    private bool _isExpanded;
    private bool _isLoadingObjects;
    private bool _hasMoreObjects;
    private int _offset;
    private string _objectsCountLabel = "";

    public ConnectionDefinition? SourceConnection { get; init; } // only for SQL rows (objects come from source)
    public string DatabaseOrService { get; init; } = "";

    // Summary columns
    public string Side { get; init; } = "";
    public string Engine { get; init; } = "";
    public string DefaultSchemaOrUser { get; init; } = "";
    public double? DatabaseSizeGb { get; init; }
    public double? DataSizeGb { get; init; }
    public double? LogOrRedoSizeGb { get; init; }

    public int? SchemaCount { get; init; }
    public int? TableCount { get; init; }
    public int? ViewCount { get; init; }
    public int? ProcedureCount { get; init; }
    public int? FunctionCount { get; init; }
    public int? SequenceCount { get; init; }
    public int? SynonymCount { get; init; }
    public int? TriggerCount { get; init; }
    public int? IndexCount { get; init; }
    public DateTimeOffset? LastStatsUpdate { get; init; }

    public ObservableCollection<InventoryObjectRowViewModel> Objects { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value)
            {
                // lazy load objects
                _ = LoadInitialAsync();
            }
        }
    }

    public bool IsLoadingObjects { get => _isLoadingObjects; set => Set(ref _isLoadingObjects, value); }
    public bool HasMoreObjects { get => _hasMoreObjects; set => Set(ref _hasMoreObjects, value); }

    public string ObjectsCountLabel { get => _objectsCountLabel; set => Set(ref _objectsCountLabel, value); }

    public AsyncRelayCommand LoadMoreCommand { get; }

    private readonly MainViewModel _main;

    public InventoryDbRowViewModel(MainViewModel main)
    {
        _main = main;
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync, () => !IsLoadingObjects && HasMoreObjects);
    }

    private async Task LoadInitialAsync()
    {
        if (SourceConnection is null) return; // Oracle row - no object drilldown for now
        if (Objects.Count > 0) return;

        _offset = 0;
        Objects.Clear();
        await LoadMoreAsync();
    }

    private async Task LoadMoreAsync()
    {
        if (SourceConnection is null) return;

        IsLoadingObjects = true;
        LoadMoreCommand.RaiseCanExecuteChanged();

        try
        {
            var services = AppServices.Current;
            if (services is null) return;

            var fetch = _main.MaxRowsPerExpand;
            var (items, hasMore) = await services.InventoryService.LoadSqlObjectsAsync(SourceConnection, DatabaseOrService, _offset, fetch, CancellationToken.None);

            foreach (var it in items)
            {
                Objects.Add(new InventoryObjectRowViewModel(it));
            }

            _offset += items.Count;
            HasMoreObjects = hasMore;
            ObjectsCountLabel = $"Loaded {Objects.Count:N0} objects";
        }
        catch (Exception ex)
        {
            _main.AppendLog($"[Inventory] Failed to load objects for {DatabaseOrService}: {ex.Message}");
            HasMoreObjects = false;
        }
        finally
        {
            IsLoadingObjects = false;
            LoadMoreCommand.RaiseCanExecuteChanged();
        }
    }
}

public sealed class InventoryObjectRowViewModel
{
    public string Schema { get; }
    public string ObjectName { get; }
    public string ObjectType { get; }
    public long? EstimatedRows { get; }
    public double? EstimatedSizeMb { get; }
    public DateTimeOffset? CreatedDate { get; }
    public DateTimeOffset? LastModifiedDate { get; }
    public int? DependsOnCount { get; }
    public int? DependedByCount { get; }
    public int ComplexityScore { get; }
    public string MigrationStatus { get; }

    public InventoryObjectRowViewModel(InventoryObjectSummary s)
    {
        Schema = s.Schema;
        ObjectName = s.ObjectName;
        ObjectType = s.ObjectType;
        EstimatedRows = s.EstimatedRows;
        EstimatedSizeMb = s.EstimatedSizeMb;
        CreatedDate = s.CreatedDate;
        LastModifiedDate = s.LastModifiedDate;
        DependsOnCount = s.DependsOnCount;
        DependedByCount = s.DependedByCount;
        ComplexityScore = s.ComplexityScore;
        MigrationStatus = s.MigrationStatus;
    }
}
