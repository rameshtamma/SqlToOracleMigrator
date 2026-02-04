using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.Views;

namespace SqlToOracleMigrator.Desktop.ViewModels;

public sealed class MainViewModel : NotifyBase
{
    private bool _initialized;
    private string _statusText = "Ready";
    private string _migrationStageText = "";
    private double _migrationProgressPercent;

    public ObservableCollection<TreeGroupNodeViewModel> ConnectionGroups { get; } = new();
    public ObservableCollection<InventoryDbRowViewModel> InventoryRows { get; } = new();
    public ObservableCollection<string> LogEntries { get; } = new();

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public string MigrationStageText { get => _migrationStageText; set => Set(ref _migrationStageText, value); }
    public double MigrationProgressPercent { get => _migrationProgressPercent; set => Set(ref _migrationProgressPercent, value); }

    public int MaxRowsPerExpand { get; private set; } = 1000;

    public AsyncRelayCommand NewConnectionCommand { get; private set; } = new(async () => await Task.CompletedTask);
    public AsyncRelayCommand RefreshInventoryCommand { get; private set; } = new(async () => await Task.CompletedTask);
    public AsyncRelayCommand CreateTargetPdbCommand { get; private set; } = new(async () => await Task.CompletedTask);
    public AsyncRelayCommand ValidateMigrationCommand { get; private set; } = new(async () => await Task.CompletedTask);
    public RelayCommand OpenLogsFolderCommand { get; private set; } = new(() => { });
    public RelayCommand ClearLogsCommand { get; private set; } = new(() => { });

    private AppServices? _services;

    // Used as a default for PDB creation (Create Target PDB button)
    private string? _lastSelectedSqlDatabaseName;

    public MainViewModel()
    {
        // Parameterless constructor required by XAML design-time.
    }

    public void Initialize(AppServices services)
    {
        if (_initialized) return;
        _initialized = true;

        _services = services ?? throw new ArgumentNullException(nameof(services));

        // Load appsettings for maxRowsPerExpand
        TryLoadAppSettings();

        NewConnectionCommand = new AsyncRelayCommand(OpenNewConnectionWizardAsync);
        RefreshInventoryCommand = new AsyncRelayCommand(RefreshInventoryAsync);
        CreateTargetPdbCommand = new AsyncRelayCommand(CreateTargetPdbAsync);
        ValidateMigrationCommand = new AsyncRelayCommand(OpenValidateMigrationAsync);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        ClearLogsCommand = new RelayCommand(() => LogEntries.Clear());

        // Subscribe logs
        _services.Logger.EntryWritten += (_, e) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var line = $"{e.Timestamp:HH:mm:ss} [{e.Level}] {e.Message}";
                LogEntries.Add(line);
                if (LogEntries.Count > 5000) LogEntries.RemoveAt(0);
            });
        };

        // Subscribe migration progress
        _services.MigrationEngine.Progress += (_, p) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MigrationStageText = $"{p.Stage}: {p.Message}";
                MigrationProgressPercent = (p.Percent ?? 0) * 100.0;
            });
        };

        LoadConnectionsTree();
        AppendLog("Application initialized.");
    }



    private async Task OpenValidateMigrationAsync()
    {
        if (_services is null) return;
        try
        {
            var win = new ValidateMigrationWindow(_services) { Owner = Application.Current?.MainWindow };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            AppendLog($"Open validation window failed: {ex.Message}");
        }

        await Task.CompletedTask;
    }
    public void AppendLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogEntries.Add($"{DateTime.Now:HH:mm:ss} {message}");
            if (LogEntries.Count > 5000) LogEntries.RemoveAt(0);
        });
    }

    private void LoadConnectionsTree()
    {
        if (_services is null) return;

        ConnectionGroups.Clear();

        var oracleGroup = new TreeGroupNodeViewModel("Oracle Connections");
        var sqlGroup = new TreeGroupNodeViewModel("SQL Server Connections");

        var defs = _services.ConnectionStore.LoadAll();

        foreach (var def in defs.Where(d => d.Engine == DatabaseEngine.Oracle))
            oracleGroup.Children.Add(new ConnectionNodeViewModel(this, def));

        foreach (var def in defs.Where(d => d.Engine == DatabaseEngine.SqlServer))
            sqlGroup.Children.Add(new ConnectionNodeViewModel(this, def));

        ConnectionGroups.Add(oracleGroup);
        ConnectionGroups.Add(sqlGroup);
    }

    public async Task OnTreeSelectionChangedAsync(object? selected)
    {
        if (_services is null) return;

        switch (selected)
        {
            case ConnectionNodeViewModel connNode:
                StatusText = $"Selected: {connNode.Definition.Name}";
                await LoadInventoryForConnectionAsync(connNode);
                break;

            case DatabaseNodeViewModel dbNode:
                StatusText = $"Selected DB: {dbNode.DatabaseName}";
                _lastSelectedSqlDatabaseName = dbNode.DatabaseName;
                await LoadInventoryForSqlDatabaseAsync(dbNode.ParentConnection, dbNode.DatabaseName);
                break;
        }
    }

    /// <summary>
    /// Double-click behavior: load inventory and expand the most relevant row so object details become visible.
    /// </summary>
    public async Task OnTreeItemDoubleClickedAsync(object? selected)
    {
        if (_services is null) return;

        switch (selected)
        {
            case ConnectionNodeViewModel connNode:
                // Ensure connected and inventory loaded
                if (!connNode.IsConnected)
                    await ConnectAsync(connNode);
                else
                    await LoadInventoryForConnectionAsync(connNode);

                InventoryDbRowViewModel? row = null;
                if (connNode.Definition.Engine == DatabaseEngine.SqlServer)
                {
                    var preferred = connNode.Definition.DefaultDatabase;
                    row = !string.IsNullOrWhiteSpace(preferred)
                        ? InventoryRows.FirstOrDefault(r => string.Equals(r.DatabaseOrService, preferred, StringComparison.OrdinalIgnoreCase))
                        : null;
                }

                row ??= InventoryRows.FirstOrDefault();
                if (row is not null)
                    row.IsExpanded = true;
                break;

            case DatabaseNodeViewModel dbNode:
                if (!dbNode.ParentConnection.IsConnected)
                    await ConnectAsync(dbNode.ParentConnection);

                await LoadInventoryForSqlDatabaseAsync(dbNode.ParentConnection, dbNode.DatabaseName);
                var dbRow = InventoryRows.FirstOrDefault();
                if (dbRow is not null)
                    dbRow.IsExpanded = true;
                break;
        }
    }

    private async Task CreateTargetPdbAsync()
    {
        if (_services is null) return;

        try
        {
            var defaultName = string.IsNullOrWhiteSpace(_lastSelectedSqlDatabaseName)
                ? "AdventureWorks2025"
                : _lastSelectedSqlDatabaseName!;

            var vm = new CreatePdbInstanceViewModel(_services, defaultName);
            var win = new CreatePdbInstanceWindow
            {
                Owner = Application.Current.MainWindow,
                DataContext = vm
            };

            var ok = win.ShowDialog();
            if (ok == true)
            {
                LoadConnectionsTree();
                StatusText = $"Created/ensured PDB '{vm.ResolvedPdbName}' and saved connection '{vm.SavedConnectionName}'.";
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Create Target PDB failed.", ex);
            MessageBox.Show(ex.Message, "Create Target PDB", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task ConnectAsync(ConnectionNodeViewModel node)
    {
        if (_services is null) return;

        try
        {
            // Ensure password in memory if needed
            await EnsurePasswordLoadedAsync(node.Definition);

            var (ok, testMsg) = await _services.ConnectionManager.TestAsync(node.Definition, CancellationToken.None);
            node.Definition.LastTestUtc = DateTimeOffset.UtcNow;
            node.Definition.LastTestStatus = ok ? ConnectionTestStatus.Green : ConnectionTestStatus.Red;
            node.Definition.LastTestMessage = testMsg;
            node.Status = node.Definition.LastTestStatus;
            _services.ConnectionStore.Save(node.Definition);

            if (!ok)
            {
                AppendLog(testMsg);
                return;
            }

            var (ok2, msg2) = await _services.ConnectionManager.ConnectAsync(node.Definition, CancellationToken.None);
            node.IsConnected = ok2;
            AppendLog(msg2);

            if (ok2 && node.Definition.Engine == DatabaseEngine.SqlServer)
            {
                // Populate database children
                await PopulateSqlDatabasesAsync(node);
            }

            await LoadInventoryForConnectionAsync(node);
        }
        catch (Exception ex)
        {
            AppendLog($"Connect failed: {ex.Message}");
            node.Definition.LastTestStatus = ConnectionTestStatus.Red;
            node.Status = ConnectionTestStatus.Red;
        }
    }

    public void Disconnect(ConnectionNodeViewModel node)
    {
        if (_services is null) return;

        try
        {
            _services.ConnectionManager.Disconnect(node.Definition);
            node.IsConnected = false;
            AppendLog($"Disconnected: {node.Definition.Name}");
        }
        catch (Exception ex)
        {
            AppendLog($"Disconnect failed: {ex.Message}");
        }
    }

    public async Task ResetAsync(ConnectionNodeViewModel node)
    {
        Disconnect(node);
        await ConnectAsync(node);
    }

    public void RemoveConnection(ConnectionNodeViewModel node)
    {
        if (_services is null) return;

        var result = MessageBox.Show($"Remove connection '{node.Definition.Name}'?\n\nThis deletes the saved connection JSON file.", "Confirm remove", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            _services.ConnectionManager.Disconnect(node.Definition);
            _services.ConnectionStore.Delete(node.Definition);
            LoadConnectionsTree();
            AppendLog($"Removed connection '{node.Definition.Name}'.");
        }
        catch (Exception ex)
        {
            AppendLog($"Remove failed: {ex.Message}");
        }
    }

    public void OpenMigrationWizard(ConnectionNodeViewModel sourceSqlConnectionNode, string databaseName)
    {
        if (_services is null) return;

        // Ensure only SQL DB nodes open migration wizard
        if (sourceSqlConnectionNode.Definition.Engine != DatabaseEngine.SqlServer) return;

        var win = new ToolDesignWizardWindow(_services, sourceSqlConnectionNode.Definition, databaseName);
        win.Owner = Application.Current.MainWindow;
        win.ShowDialog();
    }

    private async Task OpenNewConnectionWizardAsync()
    {
        if (_services is null) return;

        var win = new ConnectionWizardWindow(_services);
        win.Owner = Application.Current.MainWindow;
        var ok = win.ShowDialog();

        if (ok == true)
        {
            LoadConnectionsTree();
            AppendLog("Saved new connection.");
        }
    }

    private async Task RefreshInventoryAsync()
    {
        // For simplicity refresh current selection by reloading all inventory rows for connected nodes
        await RefreshInventoryForAllConnectedAsync();
    }

    private async Task RefreshInventoryForAllConnectedAsync()
    {
        if (_services is null) return;

        InventoryRows.Clear();

        foreach (var g in ConnectionGroups)
        {
            foreach (var node in g.Children.OfType<ConnectionNodeViewModel>())
            {
                if (_services.ConnectionManager.IsConnected(node.Definition))
                    await LoadInventoryForConnectionAsync(node);
            }
        }
    }

    private void OpenLogsFolder()
    {
        try
        {
            var dir = _services?.Paths.LogsDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { }
    }

    private async Task LoadInventoryForConnectionAsync(ConnectionNodeViewModel node)
    {
        if (_services is null) return;

        try
        {
            InventoryRows.Clear();

            if (!_services.ConnectionManager.IsConnected(node.Definition))
            {
                AppendLog($"Not connected: {node.Definition.Name}. Inventory unavailable.");
                return;
            }

            if (node.Definition.Engine == DatabaseEngine.SqlServer)
            {
                var summaries = await _services.InventoryService.LoadSqlInventoryAsync(node.Definition, CancellationToken.None);
                foreach (var s in summaries)
                {
                    InventoryRows.Add(ToVm(s, node.Definition));
                }
            }
            else
            {
                var summaries = await _services.InventoryService.LoadOracleInventoryAsync(node.Definition, CancellationToken.None);
                foreach (var s in summaries)
                {
                    InventoryRows.Add(ToVm(s, node.Definition));
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Inventory load failed: {ex.Message}");
        }
    }

    private async Task LoadInventoryForSqlDatabaseAsync(ConnectionNodeViewModel node, string dbName)
    {
        if (_services is null) return;

        try
        {
            InventoryRows.Clear();

            if (!_services.ConnectionManager.IsConnected(node.Definition))
            {
                AppendLog($"Not connected: {node.Definition.Name}. Inventory unavailable.");
                return;
            }

            var openSql = _services.ConnectionManager.TryGetOpenSql(node.Definition.Name);
            if (openSql is null) return;

            var summary = await _services.SqlMetadata.GetDbSummaryAsync(openSql, dbName, CancellationToken.None);
            InventoryRows.Add(ToVm(summary, node.Definition));
        }
        catch (Exception ex)
        {
            AppendLog($"Inventory load failed: {ex.Message}");
        }
    }

    private InventoryDbRowViewModel ToVm(InventoryDbSummary s, ConnectionDefinition? connection)
    {
        var vm = new InventoryDbRowViewModel(this)
        {
            SourceConnection = connection is not null && connection.Engine == DatabaseEngine.SqlServer ? connection : null,
            TargetConnection = connection is not null && connection.Engine == DatabaseEngine.Oracle ? connection : null,
            Side = s.Side,
            Engine = s.Engine,
            DatabaseOrService = s.DatabaseOrService,
            DefaultSchemaOrUser = s.DefaultSchemaOrUser,
            DatabaseSizeGb = s.DatabaseSizeGb,
            DataSizeGb = s.DataSizeGb,
            LogOrRedoSizeGb = s.LogOrRedoSizeGb,
            SchemaCount = s.SchemaCount,
            TableCount = s.TableCount,
            ViewCount = s.ViewCount,
            ProcedureCount = s.ProcedureCount,
            FunctionCount = s.FunctionCount,
            SequenceCount = s.SequenceCount,
            SynonymCount = s.SynonymCount,
            TriggerCount = s.TriggerCount,
            IndexCount = s.IndexCount,
            LastStatsUpdate = s.LastStatsUpdate,
            HasMoreObjects = true
        };

        // If we have no backing connection (shouldn't happen), disable drill-down.
        if (vm.SourceConnection is null && vm.TargetConnection is null)
            vm.HasMoreObjects = false;

        return vm;
    }

    private async Task EnsurePasswordLoadedAsync(ConnectionDefinition def)
    {
        if (_services is null) return;

        // If we already have an active connection, do not prompt again.
        if (_services.ConnectionManager.IsConnected(def))
            return;

        if (def.Engine == DatabaseEngine.SqlServer && def.UseWindowsAuthentication)
            return;

        if (!string.IsNullOrWhiteSpace(def.RuntimePassword))
            return;

        if (def.SavePassword && !string.IsNullOrWhiteSpace(def.EncryptedPassword))
        {
            try
            {
                def.RuntimePassword = _services.Protector.UnprotectFromBase64(def.EncryptedPassword);
                return;
            }
            catch
            {
                // fall through to prompt
            }
        }

        // Show the current username in the prompt so the user can confirm which credentials are in use.
        // Allow editing for Oracle connections (common SYS/SYSDBA scenarios) and for SQL auth (non-Windows auth).
        var allowEditUser = def.Engine == DatabaseEngine.Oracle || (def.Engine == DatabaseEngine.SqlServer && !def.UseWindowsAuthentication);
        var prompt = new PasswordPromptWindow(def.Name, def.Username ?? string.Empty, allowEditUser);
        prompt.Owner = Application.Current.MainWindow;
        if (prompt.ShowDialog() == true)
        {
            def.RuntimePassword = prompt.Password;

            // Persist updated username if the prompt allowed edits and the user changed it.
            if (allowEditUser && !string.IsNullOrWhiteSpace(prompt.UserId))
            {
                def.Username = prompt.UserId.Trim();
                _services.ConnectionStore.Save(def);
            }
        }
        else
        {
            throw new InvalidOperationException("Password is required to connect.");
        }
    }

    private async Task PopulateSqlDatabasesAsync(ConnectionNodeViewModel node)
    {
        if (_services is null) return;

        try
        {
            var openSql = _services.ConnectionManager.TryGetOpenSql(node.Definition.Name);
            if (openSql is null) return;

            var dbs = await _services.SqlMetadata.ListDatabasesAsync(openSql, CancellationToken.None);

            // Clear existing DB nodes
            node.Children.Clear();
            foreach (var db in dbs)
                node.Children.Add(new DatabaseNodeViewModel(this, node, db));
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to load databases: {ex.Message}");
        }
    }

    private void TryLoadAppSettings()
    {
        try
        {
            var file = Path.Combine(_services!.Paths.ConfigDirectory, "appsettings.json");
            if (!File.Exists(file)) return;

            var json = File.ReadAllText(file);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("limits", out var limits))
            {
                if (limits.TryGetProperty("maxRowsPerExpand", out var r) && r.TryGetInt32(out var maxRows))
                    MaxRowsPerExpand = Math.Clamp(maxRows, 100, 5000);
            }
        }
        catch
        {
            // ignore
        }
    }
}
