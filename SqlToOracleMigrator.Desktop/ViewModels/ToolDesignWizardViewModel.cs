using System.Collections.ObjectModel;
using System.Windows;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Core.Tracking;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.Views;

namespace SqlToOracleMigrator.Desktop.ViewModels;

public sealed class ToolDesignWizardViewModel : NotifyBase
{
    private readonly AppServices _services;
    private readonly ConnectionDefinition _sourceSql;
    private readonly string _sourceDatabase;

    private string? _selectedTargetOracleName;
    private string _targetSchema = "";
    private string _dop = "4";

    // v6 options
    private bool _cloneSourceSchemas = true;
    private bool _autoCreateTargetSchemas = true;
    private bool _enableDataDefValidation = true;
    private bool _enableDataValidation = true;
    private string _dataValidationRowLimit = "5000";
    private bool _validateFullDataset = false;

    // v6.2 options
    private ErrorHandlingMode _selectedErrorHandlingMode = ErrorHandlingMode.FailFast;
    private bool _resumePreviousRun = false;
    private ResumeRunOption? _selectedResumeRun;
    private string _resumeStatus = "";

    public ObservableCollection<ResumeRunOption> ResumeRuns { get; } = new();

    private string _status = "";
    private string _error = "";

    private int _currentStepIndex = 0;

    public const int LastStepIndex = 5; // 0..5

    public event Action<bool>? RequestClose;

    public ToolDesignWizardViewModel(AppServices services, ConnectionDefinition sourceSql, string sourceDatabase)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _sourceSql = sourceSql ?? throw new ArgumentNullException(nameof(sourceSql));
        _sourceDatabase = sourceDatabase ?? throw new ArgumentNullException(nameof(sourceDatabase));

        SourceConnectionName = _sourceSql.Name;
        SourceDatabase = _sourceDatabase;

        TargetOracleConnections = new ObservableCollection<string>();

        TypeMappings = new ObservableCollection<TypeMappingRow>();
        LoadTypeMappings();

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        RefreshTargetsCommand = new RelayCommand(RefreshTargets);

        BackCommand = new RelayCommand(GoBack, () => CurrentStepIndex > 0);
        NextCommand = new RelayCommand(GoNext, CanGoNext);
        FinishCommand = new AsyncRelayCommand(FinishAsync, CanFinish);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        RefreshTargets();
    }

    public string SourceConnectionName { get; }
    public string SourceDatabase { get; }

    public ObservableCollection<string> TargetOracleConnections { get; }

    public string? SelectedTargetOracleName
    {
        get => _selectedTargetOracleName;
        set
        {
            if (Set(ref _selectedTargetOracleName, value))
            {
                // Default schema to the Oracle connection username if available.
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var def = _services.ConnectionManager.GetConnected(DatabaseEngine.Oracle)
                        .FirstOrDefault(d => string.Equals(d.Name, value, StringComparison.OrdinalIgnoreCase));
                    if (def is not null && !CloneSourceSchemas && string.IsNullOrWhiteSpace(TargetSchema))
                        TargetSchema = (def.Username ?? "").Trim();
                }
                StartCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                FinishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TargetSchema
    {
        get => _targetSchema;
        set
        {
            if (Set(ref _targetSchema, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                FinishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DegreeOfParallelism
    {
        get => _dop;
        set
        {
            if (Set(ref _dop, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                FinishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CloneSourceSchemas
    {
        get => _cloneSourceSchemas;
        set
        {
            if (Set(ref _cloneSourceSchemas, value))
            {
                // In clone mode, TargetSchema is not required.
                StartCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                FinishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool AutoCreateTargetSchemas
    {
        get => _autoCreateTargetSchemas;
        set => Set(ref _autoCreateTargetSchemas, value);
    }

    public bool EnableDataDefValidation
    {
        get => _enableDataDefValidation;
        set => Set(ref _enableDataDefValidation, value);
    }

    public bool EnableDataValidation
    {
        get => _enableDataValidation;
        set => Set(ref _enableDataValidation, value);
    }

    public string DataValidationRowLimit
    {
        get => _dataValidationRowLimit;
        set => Set(ref _dataValidationRowLimit, value);
    }

    public bool ValidateFullDataset
    {
        get => _validateFullDataset;
        set => Set(ref _validateFullDataset, value);
    }

    // ----------------------------
    // v6.2: Run mode + resume
    // ----------------------------

    public Array ErrorHandlingModes => Enum.GetValues(typeof(ErrorHandlingMode));

    public ErrorHandlingMode SelectedErrorHandlingMode
    {
        get => _selectedErrorHandlingMode;
        set => Set(ref _selectedErrorHandlingMode, value);
    }

    public bool ResumePreviousRun
    {
        get => _resumePreviousRun;
        set
        {
            if (Set(ref _resumePreviousRun, value))
            {
                Raise(nameof(IsResumeEnabled));
                if (value)
                    _ = LoadResumeRunsAsync();
                else
                    SelectedResumeRun = null;
            }
        }
    }

    public bool IsResumeEnabled => ResumePreviousRun;

    public ResumeRunOption? SelectedResumeRun
    {
        get => _selectedResumeRun;
        set
        {
            if (Set(ref _selectedResumeRun, value))
            {
                // Best-effort: if a prior run contains request JSON, apply key settings to reduce mismatch.
                TryApplyRunSettings(value);
                StartCommand.RaiseCanExecuteChanged();
                FinishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ResumeStatus
    {
        get => _resumeStatus;
        set => Set(ref _resumeStatus, value);
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public string Error { get => _error; set => Set(ref _error, value); }

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand RefreshTargetsCommand { get; }

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    public AsyncRelayCommand FinishCommand { get; }
    public RelayCommand CancelCommand { get; }

    public ObservableCollection<TypeMappingRow> TypeMappings { get; }

    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        set
        {
            if (value < 0) value = 0;
            if (value > LastStepIndex) value = LastStepIndex;
            if (Set(ref _currentStepIndex, value))
            {
                BackCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                FinishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsOnLastStep => CurrentStepIndex >= LastStepIndex;

    private void GoBack() => CurrentStepIndex = Math.Max(0, CurrentStepIndex - 1);

    private void GoNext()
    {
        if (!CanGoNext()) return;
        CurrentStepIndex = Math.Min(LastStepIndex, CurrentStepIndex + 1);
    }

    private bool CanGoNext()
    {
        if (CurrentStepIndex >= LastStepIndex) return false;

        // When leaving the Target step (index 3), require required inputs.
        if (CurrentStepIndex == 3)
            return CanStart();

        return true;
    }

    private bool CanFinish()
        => IsOnLastStep && CanStart();

    private async Task FinishAsync()
    {
        await StartAsync();
        if (string.IsNullOrWhiteSpace(Error) && Status.StartsWith("Migration started", StringComparison.OrdinalIgnoreCase))
            RequestClose?.Invoke(true);
    }

    private void LoadTypeMappings()
    {
        TypeMappings.Clear();
        foreach (var kvp in _services.TypeMappings.SqlToOracle.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            TypeMappings.Add(new TypeMappingRow(kvp.Key, kvp.Value));
    }

    private void RefreshTargets()
    {
        TargetOracleConnections.Clear();

        var targets = _services.ConnectionManager.GetConnected(DatabaseEngine.Oracle)
            .Where(d => d.LastTestStatus == ConnectionTestStatus.Green)
            .Select(d => d.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var t in targets)
            TargetOracleConnections.Add(t);

        if (TargetOracleConnections.Count > 0 && string.IsNullOrWhiteSpace(SelectedTargetOracleName))
            SelectedTargetOracleName = TargetOracleConnections[0];

        if (TargetOracleConnections.Count == 0)
        {
            Status = "No connected Oracle connections in GREEN status. Connect and test an Oracle connection first.";
        }
        else
        {
            Status = "Ready to start migration.";
        }

        Error = "";
        StartCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        FinishCommand.RaiseCanExecuteChanged();
    }

    private bool CanStart()
    {
        if (string.IsNullOrWhiteSpace(SelectedTargetOracleName)) return false;

        if (ResumePreviousRun && SelectedResumeRun is null) return false;

        // In clone-mode (multi-schema), the per-schema target is derived from source schemas.
        if (!CloneSourceSchemas && string.IsNullOrWhiteSpace(TargetSchema)) return false;

        if (!int.TryParse(DegreeOfParallelism, out var dop) || dop < 1 || dop > 32) return false;

        // Data validation row limit must be numeric when not validating full dataset.
        if (!ValidateFullDataset && EnableDataValidation)
        {
            if (!int.TryParse(DataValidationRowLimit, out var lim) || lim < 0)
                return false;
        }

        return true;
    }

    private async Task EnsurePasswordLoadedAsync(ConnectionDefinition def)
    {
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
                // fall through
            }
        }

        var prompt = new PasswordPromptWindow(def.Name) { Owner = Application.Current.MainWindow };
        if (prompt.ShowDialog() == true)
        {
            def.RuntimePassword = prompt.Password;
        }
        else
        {
            throw new InvalidOperationException("Password is required to start migration.");
        }
    }

    public async Task StartAsync()
    {
        Error = "";
        try
        {
            if (!int.TryParse(DegreeOfParallelism, out var dop))
                throw new InvalidOperationException("Degree of parallelism must be a number.");

            dop = Math.Clamp(dop, 1, 32);

            var targetDef = _services.ConnectionManager.GetConnected(DatabaseEngine.Oracle)
                .FirstOrDefault(d => string.Equals(d.Name, SelectedTargetOracleName, StringComparison.OrdinalIgnoreCase));

            if (targetDef is null)
                throw new InvalidOperationException("Selected Oracle connection is not connected.");

            if (targetDef.LastTestStatus != ConnectionTestStatus.Green)
                throw new InvalidOperationException("Selected Oracle connection must be in GREEN status.");

            // Ensure passwords are available (if not using Windows Auth)
            await EnsurePasswordLoadedAsync(_sourceSql);
            await EnsurePasswordLoadedAsync(targetDef);

            // Target schema strategy:
            // - CloneSourceSchemas=true: Oracle schema name comes from each source schema (1:1). TargetSchema is not required.
            // - CloneSourceSchemas=false: all objects migrate into TargetSchema.
            string normalizedTargetSchema;
            if (CloneSourceSchemas)
            {
                normalizedTargetSchema = OracleIdent.FormatSchema((targetDef.Username ?? "SYSTEM").Trim());
            }
            else
            {
                OracleMetadataProvider.ValidateOracleIdentifier(TargetSchema);
                normalizedTargetSchema = OracleIdent.FormatSchema(TargetSchema.Trim());
            }

            var enableDdlValidation = EnableDataDefValidation;
            var enableDataValidation = EnableDataValidation;

            var validateFull = ValidateFullDataset;
            var rowLimit = 5000;
            if (!validateFull && enableDataValidation)
            {
                if (!int.TryParse(DataValidationRowLimit, out rowLimit) || rowLimit < 0)
                    throw new InvalidOperationException("Data validation row limit must be a number (>= 0).");
            }

            Status = "Starting migration...";

            var request = new MigrationRequest
            {
                SourceSqlConnection = _sourceSql,
                SourceDatabase = _sourceDatabase,
                TargetOracleConnection = targetDef,
                TargetSchema = normalizedTargetSchema,
                DegreeOfParallelism = dop,

                CloneSourceSchemas = CloneSourceSchemas,
                AutoCreateTargetSchemas = AutoCreateTargetSchemas,
                EnableDataDefValidation = enableDdlValidation,
                EnableDataValidation = enableDataValidation,
                DataValidationRowLimit = rowLimit,
                ValidateFullDataset = validateFull,

                // v6.2: strict resume + run-mode
                ResumeRunId = (ResumePreviousRun ? SelectedResumeRun?.RunId : null),
                ErrorHandlingMode = SelectedErrorHandlingMode
            };

            // Run in background so UI remains responsive; progress is surfaced in MainWindow.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _services.MigrationEngine.RunDatabaseMigrationAsync(request, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _services.Logger.Error("Migration failed.", ex);
                }
            });

            Status = "Migration started. Watch the Logs panel for progress.";

            // Move to Summary step after kickoff.
            CurrentStepIndex = LastStepIndex;
        }
        catch (Exception ex)
        {
            Status = "Cannot start migration.";
            Error = ex.Message;
        }
    }

    private async Task LoadResumeRunsAsync()
    {
        try
        {
            ResumeStatus = "Loading previous runs...";
            ResumeRuns.Clear();

            var openSql = _services.ConnectionManager.TryGetOpenSql(_sourceSql.Name);
            if (openSql is null)
            {
                ResumeStatus = "Source SQL connection is not active. Connect the source first to list runs.";
                return;
            }

            try { openSql.ChangeDatabase(SourceDatabase); } catch { }

            // Ensure schema exists; idempotent.
            await _services.ToolMigRepository.EnsureCreatedAsync(openSql, CancellationToken.None);

            var runs = await _services.ToolMigRepository.ListRunsAsync(openSql, SourceDatabase, CancellationToken.None, top: 25);
            foreach (var r in runs)
                ResumeRuns.Add(new ResumeRunOption(r));

            ResumeStatus = runs.Count == 0
                ? "No previous ToolMig runs found for this database."
                : $"Found {runs.Count} previous runs. Select one to resume.";
        }
        catch (Exception ex)
        {
            ResumeStatus = "Failed to load previous runs: " + ex.Message;
        }
    }

    private void TryApplyRunSettings(ResumeRunOption? opt)
    {
        if (opt?.RequestJson is null) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(opt.RequestJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("CloneSourceSchemas", out var v1) && v1.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                CloneSourceSchemas = v1.GetBoolean();

            if (root.TryGetProperty("AutoCreateTargetSchemas", out var v2) && v2.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                AutoCreateTargetSchemas = v2.GetBoolean();

            if (root.TryGetProperty("EnableDataDefValidation", out var v3) && v3.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                EnableDataDefValidation = v3.GetBoolean();

            if (root.TryGetProperty("EnableDataValidation", out var v4) && v4.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                EnableDataValidation = v4.GetBoolean();

            if (root.TryGetProperty("ValidateFullDataset", out var v5) && v5.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                ValidateFullDataset = v5.GetBoolean();

            if (root.TryGetProperty("DataValidationRowLimit", out var v6) && v6.TryGetInt32(out var lim))
                DataValidationRowLimit = lim.ToString();

            if (root.TryGetProperty("DegreeOfParallelism", out var v7) && v7.TryGetInt32(out var dop))
                DegreeOfParallelism = dop.ToString();

            if (root.TryGetProperty("ErrorHandlingMode", out var v8))
            {
                if (v8.ValueKind == System.Text.Json.JsonValueKind.Number && v8.TryGetInt32(out var i))
                    SelectedErrorHandlingMode = (ErrorHandlingMode)i;
                else if (v8.ValueKind == System.Text.Json.JsonValueKind.String && Enum.TryParse<ErrorHandlingMode>(v8.GetString(), true, out var m))
                    SelectedErrorHandlingMode = m;
            }

            ResumeStatus = $"Loaded settings from run v{opt.Version} (best-effort).";
        }
        catch
        {
            // ignore; resume can still proceed
        }
    }

    public sealed record TypeMappingRow(string SqlType, string OracleTemplate);

    public sealed class ResumeRunOption
    {
        public Guid RunId { get; }
        public int Version { get; }
        public DateTimeOffset StartedAt { get; }
        public string Status { get; }
        public string? TargetDatabase { get; }
        public string? RequestJson { get; }

        public string Display => $"v{Version} | {Status} | {StartedAt.LocalDateTime:g} | {(TargetDatabase ?? "-")}";

        public ResumeRunOption(ToolMigRunInfo run)
        {
            RunId = run.RunId;
            Version = run.Version;
            StartedAt = run.StartedAt;
            Status = run.Status;
            TargetDatabase = run.TargetDatabase;
            RequestJson = run.RequestJson;
        }
    }
}
