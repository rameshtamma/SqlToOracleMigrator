using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    public ConnectionDefinition? SelectedSqlConnection { get => _selectedSqlConnection; set => Set(ref _selectedSqlConnection, value); }

    private ConnectionDefinition? _selectedOracleConnection;
    public ConnectionDefinition? SelectedOracleConnection { get => _selectedOracleConnection; set => Set(ref _selectedOracleConnection, value); }

    private string _sourceDatabase = "";
    public string SourceDatabase { get => _sourceDatabase; set => Set(ref _sourceDatabase, value); }

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
    }

    private async Task RunValidationAsync()
    {
        if (_services is null) return;
        if (SelectedSqlConnection is null || SelectedOracleConnection is null)
        {
            MessageBox.Show("Please select both SQL and Oracle connections.", "Missing selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(SourceDatabase))
        {
            MessageBox.Show("Please enter the SQL source database.", "Missing database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var schemas = (SchemasCsv ?? "")
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

            var report = await validator.ValidateAsync(openSql, SourceDatabase, openOra, schemas, options, CancellationToken.None);
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
                FileName = Path.GetDirectoryName(_lastReportJsonPath)!,
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
}
