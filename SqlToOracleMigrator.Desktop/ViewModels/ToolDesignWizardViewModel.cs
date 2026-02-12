using System.Collections.ObjectModel;
using System.Windows;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Core.Tracking;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.Views;
using SqlToOracleMigrator.Desktop.ViewModels.Tree;
namespace SqlToOracleMigrator.Desktop.ViewModels;

public sealed class ToolDesignWizardViewModel : NotifyBase
{
    private readonly AppServices _services;
    private readonly ConnectionDefinition _sourceSql;
    private readonly string _sourceDatabase;

    private string? _selectedTargetOracleName;
    private string _targetSchema = "";
    private string _dop = "4";

    // v6.3 UI additions: target PDB/connection naming
    private string _targetPdbConnectionName = "";
    private bool _overrideExistingTargetPdbConnection = true;
    private bool _dropTargetPdbIfExists = true;

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

    // v1.1 pipeline UI: 4 phase groups / 10 stages
    private MigrationPlanOption _selectedPlanOption = MigrationPlanOption.Migrate;

    public ObservableCollection<MigrationPlanOption> PlanOptions { get; } = new()
    {
        MigrationPlanOption.Feasibility,
        MigrationPlanOption.DdlValidation,
        MigrationPlanOption.DataValidation,
        MigrationPlanOption.Migrate,
        MigrationPlanOption.FullMigration
    };

    public MigrationPlanOption SelectedPlanOption
    {
        get => _selectedPlanOption;
        set
        {
            if (Set(ref _selectedPlanOption, value))
            {
                BuildPipelineNodes();
            }
        }
    }

    public ObservableCollection<TreeNodeViewModel> PipelineNodes { get; } = new();



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

        // Default target PDB/connection name to the source DB name; allow user override.
        _targetPdbConnectionName = _sourceDatabase;
        _overrideExistingTargetPdbConnection = true;

        TargetOracleConnections = new ObservableCollection<string>();

        TypeMappings = new ObservableCollection<TypeMappingRow>();
        LoadTypeMappings();

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        RefreshTargetsCommand = new RelayCommand(RefreshTargets);
        ManagePdbsCommand = new AsyncRelayCommand(ManagePdbsAsync, CanManagePdbs);

        BackCommand = new RelayCommand(GoBack, () => CurrentStepIndex > 0);
        NextCommand = new RelayCommand(GoNext, CanGoNext);
        FinishCommand = new AsyncRelayCommand(FinishAsync, CanFinish);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        RefreshTargets();
        BuildPipelineNodes();
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
                ManagePdbsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Display name for the target Oracle *PDB connection* that the wizard will create/save for this migration.
    /// Defaults to the source database name (e.g., AdventureWorks2025).
    /// </summary>
    public string TargetPdbConnectionName
    {
        get => _targetPdbConnectionName;
        set
        {
            if (Set(ref _targetPdbConnectionName, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                FinishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// When true, overwrite an existing saved connection with the same TargetPdbConnectionName.
    /// When false and a name conflict exists, the wizard will automatically append _V1 / _V2 ...
    /// </summary>
    public bool OverrideExistingTargetPdbConnection
    {
        get => _overrideExistingTargetPdbConnection;
        set
        {
            if (Set(ref _overrideExistingTargetPdbConnection, value))
            {
                // Default drop behavior to match override intent.
                if (value && !DropTargetPdbIfExists)
                    DropTargetPdbIfExists = true;

                StartCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                FinishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// When true, drop the target PDB (INCLUDING DATAFILES) if it already exists, then recreate.
    /// Useful for cleaning up partial/failed runs. Requires SYSDBA.
    /// </summary>
    public bool DropTargetPdbIfExists
    {
        get => _dropTargetPdbIfExists;
        set
        {
            if (Set(ref _dropTargetPdbIfExists, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
                FinishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand ManagePdbsCommand { get; }

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

        if (string.IsNullOrWhiteSpace(TargetPdbConnectionName)) return false;

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

        var allowEditUser = def.Engine == DatabaseEngine.Oracle || (def.Engine == DatabaseEngine.SqlServer && !def.UseWindowsAuthentication);
        var prompt = new PasswordPromptWindow(def.Name, def.Username ?? string.Empty, allowEditUser) { Owner = Application.Current.MainWindow };
        if (prompt.ShowDialog() != true)
            throw new InvalidOperationException("Password is required to start migration.");

        var prevUser = def.Username ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(prompt.UserId) && !string.Equals(prevUser, prompt.UserId, StringComparison.Ordinal))
        {
            def.Username = prompt.UserId;
            _services.ConnectionStore.Save(def);
        }

        def.RuntimePassword = prompt.Password;

        if (def.SavePassword)
        {
            def.EncryptedPassword = _services.Protector.ProtectToBase64(def.RuntimePassword);
            _services.ConnectionStore.Save(def);
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

            // New flow: Target PDB is created ahead of time via MainWindow "Create Target PDB".
            // Migration requires that the selected Oracle connection is already pointing at a PDB (not CDB$ROOT).
            try
            {
                await using var open = ConnectionStringBuilders.CreateOpenOracleConnection(targetDef);
                var conName = await OraclePdbAdmin.GetCurrentContainerAsync(open, CancellationToken.None);
                if (string.Equals(conName, "CDB$ROOT", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Selected Oracle connection is pointing at CDB$ROOT (XE/root). " +
                        "Please use the 'Create Target PDB' button in the main window to create a PDB and save a PDB connection, " +
                        "then select that PDB connection here before starting migration.");
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch
            {
                // If we cannot determine container, proceed; downstream DDL will fail with a clearer error.
            }

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
                ErrorHandlingMode = SelectedErrorHandlingMode,

                // New flow: caller must already select a PDB connection.
                EnsureTargetPdb = false,
                DropTargetPdbIfExists = false
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

            if (root.TryGetProperty("TargetPdbName", out var v9) && v9.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var pdb = v9.GetString();
                if (!string.IsNullOrWhiteSpace(pdb))
                    TargetPdbConnectionName = pdb!;
            }

            ResumeStatus = $"Loaded settings from run v{opt.Version} (best-effort).";
        }
        catch
        {
            // ignore; resume can still proceed
        }
    }

    // ----------------------------
    // v6.3 helpers (PDB connection naming)
    // ----------------------------
    private static string NormalizeOracleIdentifierForPdb(string input)
    {
        var raw = input.Trim();
        // Keep it simple and deterministic: replace whitespace with underscores.
        raw = string.Join("_", raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        OracleMetadataProvider.ValidateOracleIdentifier(raw);
        return OracleIdent.FormatSchema(raw);
    }

    private static string ResolveUniqueName(string desired, IEnumerable<string> existingNames, bool allowOverwrite)
    {
        var desiredTrim = desired.Trim();
        if (string.IsNullOrWhiteSpace(desiredTrim))
            return desiredTrim;

        var set = new HashSet<string>(existingNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        if (allowOverwrite || !set.Contains(desiredTrim))
            return desiredTrim;

        // Requirement: append _V1 if conflict exists.
        var baseName = desiredTrim;
        for (var i = 1; i <= 99; i++)
        {
            var candidate = $"{baseName}_V{i}";
            if (!set.Contains(candidate))
                return candidate;
        }
        // Extremely unlikely; fallback to GUID suffix.
        return $"{baseName}_V{Guid.NewGuid():N}";
    }

    private static ConnectionDefinition CreatePdbConnectionDefinition(string connectionName, string pdbName, ConnectionDefinition from)
    {
        // Create a new connection definition pointing at the PDB service name.
        return new ConnectionDefinition
        {
            Name = connectionName,
            Engine = DatabaseEngine.Oracle,

            Hostname = from.Hostname,
            Port = from.Port,

            Username = from.Username,
            UseWindowsAuthentication = false,
            SavePassword = from.SavePassword,
            EncryptedPassword = from.EncryptedPassword,
            RuntimePassword = from.RuntimePassword,

            AuthenticationType = from.AuthenticationType,
            ConnectionType = from.ConnectionType,
            Role = from.Role,

            // Service-based connect for PDB
            UseSid = false,
            Sid = null,
            ServiceName = pdbName,

            Region = from.Region,
            Notes = from.Notes,
            Color = from.Color
        };
    }

    // ----------------------------
    // PDB Manager (utility)
    // ----------------------------
    private bool CanManagePdbs()
    {
        if (string.IsNullOrWhiteSpace(SelectedTargetOracleName)) return false;
        var def = _services.ConnectionManager.GetConnected(DatabaseEngine.Oracle)
            .FirstOrDefault(d => string.Equals(d.Name, SelectedTargetOracleName, StringComparison.OrdinalIgnoreCase));
        if (def is null) return false;
        if (def.LastTestStatus != ConnectionTestStatus.Green) return false;
        return IsOracleAdminConnection(def);
    }

    private static bool IsOracleAdminConnection(ConnectionDefinition def)
    {
        var u = (def.Username ?? "").Trim();
        var r = (def.Role ?? "").Trim();
        // Accept either SYS user, or explicit SYSDBA role.
        if (u.Equals("SYS", StringComparison.OrdinalIgnoreCase)) return true;
        if (r.Equals("SYSDBA", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task ManagePdbsAsync()
    {
        Error = "";
        try
        {
            var targetDef = _services.ConnectionManager.GetConnected(DatabaseEngine.Oracle)
                .FirstOrDefault(d => string.Equals(d.Name, SelectedTargetOracleName, StringComparison.OrdinalIgnoreCase));
            if (targetDef is null)
                throw new InvalidOperationException("Selected Oracle connection is not connected.");

            if (!IsOracleAdminConnection(targetDef))
                throw new InvalidOperationException("PDB Manager requires SYS/SYSDBA. Select an Oracle connection that uses SYS or SYSDBA role.");

            // Ensure the connection is open.
            var open = _services.ConnectionManager.TryGetOpenOracle(targetDef.Name);
            if (open is null)
                throw new InvalidOperationException("Oracle connection is not active. Connect it first from the Connections panel.");

            var vm = new PdbManagerViewModel(_services, targetDef.Name);
            var win = new PdbManagerWindow
            {
                Owner = Application.Current.MainWindow,
                DataContext = vm
            };

            win.ShowDialog();
            await Task.Yield();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
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

    private void BuildPipelineNodes()
    {
        PipelineNodes.Clear();

        // Always show full 10-stage pipeline grouped into 4 major categories (your demo expectation).
        PipelineNodes.Add(MakeGroup("Connect & Assess", 
            "1. Connection Fingerprinting",
            "2. Deep Discovery",
            "3. Planning & Topological Graph"));

        PipelineNodes.Add(MakeGroup("Plan & Prepare", 
            "4. Provisioning",
            "5. DDL Generation & Dry Run",
            "6. Skeleton Deployment"));

        PipelineNodes.Add(MakeGroup("Build & Load", 
            "7. Data Strategy & Sampling",
            "8. Parallel Data Migration (OracleBulkCopy)"));

        PipelineNodes.Add(MakeGroup("Enforce & Verify", 
            "9. Post-Load Enforcement (Convert → Constraints/Indexes → Stats)",
            "10. Final Verification (Strict Security Replication)"));

        // Helper method updated to disambiguate constructor call
        TreeGroupNodeViewModel MakeGroup(string name, params string[] stages)
        {
            var g = (TreeGroupNodeViewModel)Activator.CreateInstance(typeof(TreeGroupNodeViewModel), name)!;
            foreach (var s in stages) g.Children.Add(new SimpleLeafNodeViewModel(s));
            return g;
        }
    }


}
