using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Data.SqlClient;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Core.Validation;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.Views;

namespace SqlToOracleMigrator.Desktop.ViewModels;

public sealed class ValidateMigrationViewModel : NotifyBase
{
    private AppServices? _services;

    public ObservableCollection<ConnectionDefinition> SqlConnections { get; } = new();
    public ObservableCollection<ConnectionDefinition> OracleConnections { get; } = new();

    private ConnectionDefinition? _selectedSqlConnection;
    public ConnectionDefinition? SelectedSqlConnection
    {
        get => _selectedSqlConnection;
        set
        {
            if (Set(ref _selectedSqlConnection, value))
            {
                // Auto-load DBs when the SQL connection changes.
                _ = RefreshDatabasesAsync();
            }
        }
    }

    private ConnectionDefinition? _selectedOracleConnection;
    public ConnectionDefinition? SelectedOracleConnection { get => _selectedOracleConnection; set => Set(ref _selectedOracleConnection, value); }

    private string _sourceDatabase = "";
    public string SourceDatabase { get => _sourceDatabase; set => Set(ref _sourceDatabase, value); }

    public ObservableCollection<string> SourceDatabases { get; } = new();

    private string? _selectedSourceDatabase;
    public string? SelectedSourceDatabase
    {
        get => _selectedSourceDatabase;
        set
        {
            if (Set(ref _selectedSourceDatabase, value))
            {
                if (!string.IsNullOrWhiteSpace(value))
                    SourceDatabase = value;
                _ = RefreshSchemasAsync();
            }
        }
    }

    private bool _isLoadingDatabases;
    public bool IsLoadingDatabases { get => _isLoadingDatabases; set => Set(ref _isLoadingDatabases, value); }

    private string _databasesStatusText = "";
    public string DatabasesStatusText { get => _databasesStatusText; set => Set(ref _databasesStatusText, value); }

    public ObservableCollection<SchemaSelectionViewModel> Schemas { get; } = new();

    private bool _isLoadingSchemas;
    public bool IsLoadingSchemas { get => _isLoadingSchemas; set => Set(ref _isLoadingSchemas, value); }

    private string _schemasStatusText = "";
    public string SchemasStatusText { get => _schemasStatusText; set => Set(ref _schemasStatusText, value); }

    private string _schemasCsv = "dbo";
    public string SchemasCsv { get => _schemasCsv; set => Set(ref _schemasCsv, value); }

    private bool _includeRowCounts = true;
    public bool IncludeRowCounts { get => _includeRowCounts; set => Set(ref _includeRowCounts, value); }

    private bool _includeKeysAndInvalidChecks = true;
    public bool IncludeKeysAndInvalidChecks { get => _includeKeysAndInvalidChecks; set => Set(ref _includeKeysAndInvalidChecks, value); }

    private string _reportOutputFolder = "";
    public string ReportOutputFolder { get => _reportOutputFolder; set => Set(ref _reportOutputFolder, value); }

    private string _statusText = "Ready";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _summaryText = "";
    public string SummaryText { get => _summaryText; set => Set(ref _summaryText, value); }

    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; set => Set(ref _progressPercent, value); }

    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public AsyncRelayCommand RunValidationCommand { get; private set; } = new(async () => await Task.CompletedTask);
    public RelayCommand OpenLastReportCommand { get; private set; } = new(() => { });
    public AsyncRelayCommand RefreshDatabasesCommand { get; private set; } = new(async () => await Task.CompletedTask);
    public RelayCommand SelectAllSchemasCommand { get; private set; } = new(() => { });
    public RelayCommand SelectNoneSchemasCommand { get; private set; } = new(() => { });

    private string? _lastReportJsonPath;

    public ValidateMigrationViewModel()
    {
        // XAML design-time
    }

    public void Initialize(AppServices services)
    {
        _services = services;

        ReportOutputFolder = services.Paths.LogsDirectory;

        SqlConnections.Clear();
        OracleConnections.Clear();

        var defs = services.ConnectionStore.LoadAll();
        foreach (var d in defs.Where(d => d.Engine == DatabaseEngine.SqlServer).OrderByDescending(d => d.LastTestUtc ?? DateTimeOffset.MinValue))
            SqlConnections.Add(d);
        foreach (var d in defs.Where(d => d.Engine == DatabaseEngine.Oracle).OrderByDescending(d => d.LastTestUtc ?? DateTimeOffset.MinValue))
            OracleConnections.Add(d);

        SelectedSqlConnection = SqlConnections.FirstOrDefault();
        SelectedOracleConnection = OracleConnections.FirstOrDefault();

        // Default DB name from SQL connection if present.
        SourceDatabase = SelectedSqlConnection?.DefaultDatabase ?? "";

        RunValidationCommand = new AsyncRelayCommand(RunValidationAsync);
        OpenLastReportCommand = new RelayCommand(OpenLastReport);

        RefreshDatabasesCommand = new AsyncRelayCommand(RefreshDatabasesAsync);
        SelectAllSchemasCommand = new RelayCommand(() => SetAllSchemas(true));
        SelectNoneSchemasCommand = new RelayCommand(() => SetAllSchemas(false));

        // Auto-populate DBs + schemas on load.
        _ = RefreshDatabasesAsync();
    }

    private async Task RunValidationAsync()
    {
        if (_services is null) return;
        if (SelectedSqlConnection is null || SelectedOracleConnection is null)
        {
            MessageBox.Show("Please select both SQL and Oracle connections.", "Missing selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sourceDb = !string.IsNullOrWhiteSpace(SelectedSourceDatabase) ? SelectedSourceDatabase : SourceDatabase;
        if (string.IsNullOrWhiteSpace(sourceDb))
        {
            MessageBox.Show("Please enter the SQL source database.", "Missing database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedSchemas = Schemas.Where(s => s.IsSelected).Select(s => s.Name).ToList();
        var schemas = selectedSchemas.Count > 0
            ? selectedSchemas
            : (SchemasCsv ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (schemas.Count == 0)
        {
            MessageBox.Show("Please enter at least one schema.", "Missing schemas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Issues.Clear();
            SummaryText = "";
            StatusText = "Connecting...";
            ProgressPercent = 5;

            await EnsurePasswordLoadedAsync(SelectedSqlConnection);
            await EnsurePasswordLoadedAsync(SelectedOracleConnection);

            // Ensure connected
            if (!_services.ConnectionManager.IsConnected(SelectedSqlConnection))
                await _services.ConnectionManager.ConnectAsync(SelectedSqlConnection, CancellationToken.None);
            if (!_services.ConnectionManager.IsConnected(SelectedOracleConnection))
                await _services.ConnectionManager.ConnectAsync(SelectedOracleConnection, CancellationToken.None);

            var openSql = _services.ConnectionManager.TryGetOpenSql(SelectedSqlConnection.Name);
            var openOra = _services.ConnectionManager.TryGetOpenOracle(SelectedOracleConnection.Name);
            if (openSql is null || openOra is null)
                throw new InvalidOperationException("Failed to obtain open connections.");

            StatusText = "Running validation...";
            ProgressPercent = 15;

            var validator = new PostMigrationValidator(_services.Logger);
            var options = new PostMigrationValidationOptions
            {
                IncludeRowCounts = IncludeRowCounts,
                IncludeKeyAndInvalidChecks = IncludeKeysAndInvalidChecks,
                RowCountParallelism = 4,
                RowCountCommandTimeoutSeconds = 120
            };

            var report = await validator.ValidateAsync(openSql, sourceDb!, openOra, schemas, options, CancellationToken.None);
            ProgressPercent = 95;

            var outDir = string.IsNullOrWhiteSpace(ReportOutputFolder) ? _services.Paths.LogsDirectory : ReportOutputFolder;
            _lastReportJsonPath = await PostMigrationValidator.SaveReportAsync(report, outDir, CancellationToken.None);

            foreach (var issue in report.Issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Category).ThenBy(i => i.Schema).ThenBy(i => i.Name))
                Issues.Add(issue);

            SummaryText = $"SQL Objects={report.Summary.SourceObjectCount}, ORA Objects={report.Summary.TargetObjectCount}, Errors={report.Summary.ErrorCount}, Warnings={report.Summary.WarnCount}";
            StatusText = $"Validation complete. Report: {_lastReportJsonPath}";
            ProgressPercent = 100;
        }
        catch (Exception ex)
        {
            StatusText = $"Validation failed: {ex.Message}";
            ProgressPercent = 0;
            _services.Logger.Error("Post migration validation failed.", ex);
            MessageBox.Show(ex.ToString(), "Validation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLastReport()
    {
        if (string.IsNullOrWhiteSpace(_lastReportJsonPath) || !File.Exists(_lastReportJsonPath))
        {
            MessageBox.Show("No report has been generated yet.", "Open report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _lastReportJsonPath!,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    private async Task EnsurePasswordLoadedAsync(ConnectionDefinition def)
    {
        if (_services is null) return;

        // If we already have an active connection, do not prompt again.
        if (_services.ConnectionManager.IsConnected(def))
            return;
        if (def.Engine == DatabaseEngine.SqlServer && def.UseWindowsAuthentication)
            return;

        if (!string.IsNullOrWhiteSpace(def.RuntimePassword)) return;

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

        // Prompt the user
        var win = new PasswordPromptWindow(def.Name) { Owner = Application.Current?.MainWindow };
        if (win.ShowDialog() != true)
            throw new InvalidOperationException("Password is required.");

        def.RuntimePassword = win.Password;

        // Persist if the user configured SavePassword (behavior consistent with connection wizard)
        if (def.SavePassword)
        {
            def.EncryptedPassword = _services.Protector.ProtectToBase64(def.RuntimePassword);
            _services.ConnectionStore.Save(def);
        }

        await Task.CompletedTask;
    }

    private void SetAllSchemas(bool selected)
    {
        foreach (var s in Schemas)
            s.IsSelected = selected;
    }

    private async Task RefreshDatabasesAsync()
    {
        if (_services is null) return;
        if (SelectedSqlConnection is null)
        {
            SourceDatabases.Clear();
            SelectedSourceDatabase = null;
            return;
        }

        try
        {
            IsLoadingDatabases = true;
            DatabasesStatusText = "Loading databases...";

            // Ensure connected (reuses active connection if already connected)
            await EnsurePasswordLoadedAsync(SelectedSqlConnection);
            if (!_services.ConnectionManager.IsConnected(SelectedSqlConnection))
                await _services.ConnectionManager.ConnectAsync(SelectedSqlConnection, CancellationToken.None);

            var openSql = _services.ConnectionManager.TryGetOpenSql(SelectedSqlConnection.Name);
            if (openSql is null)
                throw new InvalidOperationException("Failed to obtain an open SQL connection.");

            var dbs = await _services.SqlMetadata.ListDatabasesAsync(openSql, CancellationToken.None);
            Application.Current.Dispatcher.Invoke(() =>
            {
                SourceDatabases.Clear();
                foreach (var db in dbs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    SourceDatabases.Add(db);
            });

            var preferred = SelectedSqlConnection.DefaultDatabase;
            var chosen = !string.IsNullOrWhiteSpace(preferred) && dbs.Contains(preferred, StringComparer.OrdinalIgnoreCase)
                ? preferred
                : dbs.FirstOrDefault();

            SelectedSourceDatabase = chosen;
            if (!string.IsNullOrWhiteSpace(chosen))
                SourceDatabase = chosen;

            DatabasesStatusText = "";
        }
        catch (Exception ex)
        {
            DatabasesStatusText = $"Failed to load databases: {ex.Message}";
            _services.Logger.Warn($"[Validation] Load databases failed: {ex.Message}");
        }
        finally
        {
            IsLoadingDatabases = false;
        }
    }

    private async Task RefreshSchemasAsync()
    {
        if (_services is null) return;
        var db = !string.IsNullOrWhiteSpace(SelectedSourceDatabase) ? SelectedSourceDatabase : SourceDatabase;
        if (SelectedSqlConnection is null || string.IsNullOrWhiteSpace(db))
        {
            Schemas.Clear();
            return;
        }

        try
        {
            IsLoadingSchemas = true;
            SchemasStatusText = "Loading schemas...";

            await EnsurePasswordLoadedAsync(SelectedSqlConnection);
            if (!_services.ConnectionManager.IsConnected(SelectedSqlConnection))
                await _services.ConnectionManager.ConnectAsync(SelectedSqlConnection, CancellationToken.None);

            var openSql = _services.ConnectionManager.TryGetOpenSql(SelectedSqlConnection.Name);
            if (openSql is null)
                throw new InvalidOperationException("Failed to obtain an open SQL connection.");

            var dbBracketed = SqlToOracleMigrator.Core.SqlIdent.Bracket(db);
            var sql = $@"SELECT name
FROM {dbBracketed}.sys.schemas
WHERE principal_id < 16384
ORDER BY name";

            var list = new List<string>();
            await using (var cmd = new SqlCommand(sql, openSql))
            await using (var rdr = await cmd.ExecuteReaderAsync(CancellationToken.None))
            {
                while (await rdr.ReadAsync(CancellationToken.None))
                    list.Add(rdr.GetString(0));
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                Schemas.Clear();
                foreach (var s in list)
                    Schemas.Add(new SchemaSelectionViewModel(s, true));
            });

            SchemasStatusText = "";
        }
        catch (Exception ex)
        {
            SchemasStatusText = $"Failed to load schemas: {ex.Message}";
            _services.Logger.Warn($"[Validation] Load schemas failed: {ex.Message}");
        }
        finally
        {
            IsLoadingSchemas = false;
        }
    }
}

public sealed class SchemaSelectionViewModel : NotifyBase
{
    private bool _isSelected;
    public string Name { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public SchemaSelectionViewModel(string name, bool selected)
    {
        Name = name;
        _isSelected = selected;
    }
}
