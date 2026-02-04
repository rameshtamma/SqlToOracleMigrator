using System.Collections.ObjectModel;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

    private CancellationTokenSource? _loadCts;

    /// <summary>
    /// Source SQL connection (when Side==Source). Used for drill-down to objects.
    /// </summary>
    public ConnectionDefinition? SourceConnection { get; init; }

    /// <summary>
    /// Target Oracle connection (when Side==Target). Used for drill-down to objects.
    /// </summary>
    public ConnectionDefinition? TargetConnection { get; init; }

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
            if (Set(ref _isExpanded, value))
            {
                if (value)
                {
                    // lazy load objects (non-blocking)
                    _ = LoadInitialAsync();
                }
                else
                {
                    // cancel in-flight loads when user collapses
                    try { _loadCts?.Cancel(); } catch { }
                }
            }
        }
    }

    public bool IsLoadingObjects { get => _isLoadingObjects; private set => Set(ref _isLoadingObjects, value); }

    /// <summary>
    /// Indicates whether the server-side inventory query has more objects beyond what is currently loaded.
    /// 
    /// NOTE: This is intentionally <c>internal set</c> because other view models (e.g., MainViewModel)
    /// may need to update this flag when refreshing rows, while still preventing external callers
    /// (outside the Desktop assembly) from mutating it.
    /// </summary>
    public bool HasMoreObjects { get => _hasMoreObjects; internal set => Set(ref _hasMoreObjects, value); }

    public string ObjectsCountLabel { get => _objectsCountLabel; private set => Set(ref _objectsCountLabel, value); }

    public AsyncRelayCommand LoadMoreCommand { get; }

    private readonly MainViewModel _main;

    public InventoryDbRowViewModel(MainViewModel main)
    {
        _main = main;
        LoadMoreCommand = new AsyncRelayCommand(() => LoadMoreAsync(ensureReset: false), () => !IsLoadingObjects && HasMoreObjects);
    }

    private async Task LoadInitialAsync()
    {
        if (SourceConnection is null && TargetConnection is null) return;
        if (IsLoadingObjects) return;

        _offset = 0;

        // cancel any prior load and start a fresh CTS
        try { _loadCts?.Cancel(); } catch { }
        try { _loadCts?.Dispose(); } catch { }
        _loadCts = new CancellationTokenSource();

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        await dispatcher.InvokeAsync(() => Objects.Clear(), DispatcherPriority.Background);

        await LoadMoreAsync(ensureReset: true);
    }

    private async Task LoadMoreAsync(bool ensureReset)
    {
        if (SourceConnection is null && TargetConnection is null) return;
        if (IsLoadingObjects) return;

        _loadCts ??= new CancellationTokenSource();
        var ct = _loadCts.Token;

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        await dispatcher.InvokeAsync(() =>
        {
            IsLoadingObjects = true;
            LoadMoreCommand.RaiseCanExecuteChanged();
        }, DispatcherPriority.Background);

        try
        {
            var services = AppServices.Current;
            if (services is null) return;

            var fetch = _main.MaxRowsPerExpand;

            IReadOnlyList<InventoryObjectSummary> items;
            bool hasMore;
            if (SourceConnection is not null)
            {
                (items, hasMore) = await services.InventoryService
                    .LoadSqlObjectsAsync(SourceConnection, DatabaseOrService, _offset, fetch, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                (items, hasMore) = await services.InventoryService
                    .LoadOracleObjectsAsync(TargetConnection!, _offset, fetch, ct)
                    .ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // Sort/group friendly ordering: type then name then schema
            var viewModels = items
                .OrderBy(i => i.ObjectType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Schema, StringComparer.OrdinalIgnoreCase)
                .Select(i => new InventoryObjectRowViewModel(i))
                .ToList();

            // Batch UI updates to keep scrolling responsive.
            const int batchSize = 250;
            for (var i = 0; i < viewModels.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = viewModels.Skip(i).Take(batchSize).ToList();

                await dispatcher.InvokeAsync(() =>
                {
                    foreach (var vm in chunk)
                        Objects.Add(vm);
                }, DispatcherPriority.Background);

                // Let UI breathe between batches
                await Task.Yield();
            }

            _offset += items.Count;

            await dispatcher.InvokeAsync(() =>
            {
                HasMoreObjects = hasMore;
                ObjectsCountLabel = $"Loaded {Objects.Count:N0} objects";
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // user collapsed row or navigation changed
            await dispatcher.InvokeAsync(() =>
            {
                HasMoreObjects = false;
                ObjectsCountLabel = $"Loaded {Objects.Count:N0} objects (cancelled)";
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _main.AppendLog($"[Inventory] Failed to load objects for {DatabaseOrService}: {ex.Message}");
            await dispatcher.InvokeAsync(() => HasMoreObjects = false, DispatcherPriority.Background);
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                IsLoadingObjects = false;
                LoadMoreCommand.RaiseCanExecuteChanged();
            }, DispatcherPriority.Background);
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
