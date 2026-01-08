using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Desktop.Services;

namespace SqlToOracleMigrator.Desktop.ViewModels;

public abstract class TreeNodeViewModel : NotifyBase
{
    public string DisplayName { get; protected set; } = "";
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();
}

public sealed class TreeGroupNodeViewModel : TreeNodeViewModel
{
    public TreeGroupNodeViewModel(string name)
    {
        DisplayName = name;
    }
}

public sealed class ConnectionNodeViewModel : TreeNodeViewModel
{
    private ConnectionTestStatus _status;
    private bool _isConnected;

    public ConnectionDefinition Definition { get; }
    public ConnectionTestStatus Status { get => _status; set => Set(ref _status, value); }
    public bool IsConnected { get => _isConnected; set => Set(ref _isConnected, value); }

    public AsyncRelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }

    private readonly MainViewModel _main;

    public ConnectionNodeViewModel(MainViewModel main, ConnectionDefinition def)
    {
        _main = main;
        Definition = def;
        DisplayName = def.Name;
        Status = def.LastTestStatus;

        ConnectCommand = new AsyncRelayCommand(async () => await _main.ConnectAsync(this));
        ResetCommand = new AsyncRelayCommand(async () => await _main.ResetAsync(this));
        DisconnectCommand = new RelayCommand(() => _main.Disconnect(this));
        RemoveCommand = new RelayCommand(() => _main.RemoveConnection(this));
        OpenConfigFolderCommand = new RelayCommand(() =>
        {
            try
            {
                var dir = AppServices.Current?.Paths.ConnectionsDirectory ?? "";
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch { }
        });
    }
}

public sealed class DatabaseNodeViewModel : TreeNodeViewModel
{
    public string DatabaseName { get; }
    public ConnectionNodeViewModel ParentConnection { get; }
    public bool IsSqlDatabaseNode => ParentConnection.Definition.Engine == DatabaseEngine.SqlServer;

    public RelayCommand MigrateDataCommand { get; }

    private readonly MainViewModel _main;

    public DatabaseNodeViewModel(MainViewModel main, ConnectionNodeViewModel parent, string databaseName)
    {
        _main = main;
        ParentConnection = parent;
        DatabaseName = databaseName;
        DisplayName = databaseName;

        MigrateDataCommand = new RelayCommand(() => _main.OpenMigrationWizard(parent, databaseName));
    }
}
