using System.Collections.ObjectModel;
using System.Windows;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Desktop.Services;

namespace SqlToOracleMigrator.Desktop.ViewModels;

public sealed class ConnectionWizardViewModel : NotifyBase
{
    private readonly AppServices _services;

    private string _connectionName = "";
    private string _selectedEnvironment = "Dev";

    private bool _isSqlSelected;
    private bool _isOracleSelected;

    private string _hostname = "";
    private string _port = "";
    private string _username = "";
    private bool _useWindowsAuth;
    private bool _savePassword = true;

    private string? _defaultDatabase;
    private string _statusMessage = "Select database type.";
    private string _errorDetails = "";

    // Oracle fields
    private string _authenticationType = "Default";
    private string _connectionType = "Basic";
    private string _role = "default";
    private bool _useSid = true;
    private bool _useServiceName;
    private string _sid = "";
    private string _serviceName = "";

    public ConnectionWizardViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Environments = new ObservableCollection<string>(new[] { "Dev", "Uat", "Prod" });
        AuthTypeOptions = new ObservableCollection<string>(_services.AuthTypes.Count == 0 ? new[] { "Default" } : _services.AuthTypes);
        ConnectionTypeOptions = new ObservableCollection<string>(_services.ConnectionTypes.Count == 0 ? new[] { "Basic" } : _services.ConnectionTypes);
        RoleOptions = new ObservableCollection<string>(new[] { "default", "SYSDBA", "SYSOPER" });

        DatabaseOptions = new ObservableCollection<string>();

        TestCommand = new AsyncRelayCommand(TestAsync, CanTestOrSave);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanTestOrSave);
        RetrieveDatabasesCommand = new AsyncRelayCommand(RetrieveDatabasesAsync, () => IsSqlSelected);
        ClearCommand = new RelayCommand(Clear);
    }

    // Design-time only (required for XAML designer). Not used at runtime.
    public ConnectionWizardViewModel() : this(AppServices.Current ?? throw new InvalidOperationException("AppServices not initialized.")) { }

    public string ConnectionName { get => _connectionName; set { Set(ref _connectionName, value); RaiseCanExec(); } }
    public ObservableCollection<string> Environments { get; }
    public string SelectedEnvironment { get => _selectedEnvironment; set => Set(ref _selectedEnvironment, value); }

    public bool IsSqlSelected
    {
        get => _isSqlSelected;
        set
        {
            if (Set(ref _isSqlSelected, value))
            {
                if (value) IsOracleSelected = false;
                ApplyEngineDefaults();
                RaiseCanExec();
            }
        }
    }

    public bool IsOracleSelected
    {
        get => _isOracleSelected;
        set
        {
            if (Set(ref _isOracleSelected, value))
            {
                if (value) IsSqlSelected = false;
                ApplyEngineDefaults();
                RaiseCanExec();
            }
        }
    }

    public Visibility SqlSectionVisible => IsSqlSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OracleSectionVisible => IsOracleSelected ? Visibility.Visible : Visibility.Collapsed;

    public string Hostname { get => _hostname; set { Set(ref _hostname, value); RaiseCanExec(); } }
    public string Port { get => _port; set { Set(ref _port, value); RaiseCanExec(); } }
    public string Username { get => _username; set { Set(ref _username, value); RaiseCanExec(); } }
    public bool UseWindowsAuth { get => _useWindowsAuth; set { Set(ref _useWindowsAuth, value); RaiseCanExec(); } }
    public bool SavePassword { get => _savePassword; set => Set(ref _savePassword, value); }

    public ObservableCollection<string> DatabaseOptions { get; }
    public string? DefaultDatabase { get => _defaultDatabase; set => Set(ref _defaultDatabase, value); }

    public ObservableCollection<string> AuthTypeOptions { get; }
    public ObservableCollection<string> ConnectionTypeOptions { get; }
    public ObservableCollection<string> RoleOptions { get; }

    public string AuthenticationType { get => _authenticationType; set => Set(ref _authenticationType, value); }
    public string ConnectionType { get => _connectionType; set => Set(ref _connectionType, value); }
    public string Role { get => _role; set => Set(ref _role, value); }

    public bool UseSid
    {
        get => _useSid;
        set
        {
            if (Set(ref _useSid, value))
            {
                if (value) UseServiceName = false;
                RaiseCanExec();
            }
        }
    }

    public bool UseServiceName
    {
        get => _useServiceName;
        set
        {
            if (Set(ref _useServiceName, value))
            {
                if (value) UseSid = false;
                RaiseCanExec();
            }
        }
    }

    public string Sid { get => _sid; set { Set(ref _sid, value); RaiseCanExec(); } }
    public string ServiceName { get => _serviceName; set { Set(ref _serviceName, value); RaiseCanExec(); } }

    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public string ErrorDetails { get => _errorDetails; set => Set(ref _errorDetails, value); }

    /// <summary>
    /// Password is set by the window code-behind from PasswordBox. Never persisted.
    /// </summary>
    public string Password { get; set; } = "";

    public AsyncRelayCommand TestCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RetrieveDatabasesCommand { get; }
    public RelayCommand ClearCommand { get; }

    private void RaiseCanExec()
    {
        OnPropertyChanged(nameof(SqlSectionVisible));
        OnPropertyChanged(nameof(OracleSectionVisible));
        TestCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        RetrieveDatabasesCommand.RaiseCanExecuteChanged();
    }

    private bool CanTestOrSave()
    {
        if (!IsSqlSelected && !IsOracleSelected) return false;
        if (string.IsNullOrWhiteSpace(ConnectionName)) return false;
        if (string.IsNullOrWhiteSpace(Hostname)) return false;
        if (!int.TryParse(Port, out var p) || p < 1 || p > 65535) return false;

        if (IsSqlSelected)
        {
            if (UseWindowsAuth) return true;
            return !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
        }

        // Oracle
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password)) return false;
        if (UseSid) return !string.IsNullOrWhiteSpace(Sid);
        return !string.IsNullOrWhiteSpace(ServiceName);
    }

    private void ApplyEngineDefaults()
    {
        ErrorDetails = "";

        if (IsSqlSelected)
        {
            if (string.IsNullOrWhiteSpace(Port)) Port = "1433";
            StatusMessage = "SQL Server connection.";
            return;
        }

        if (IsOracleSelected)
        {
            if (string.IsNullOrWhiteSpace(Port)) Port = "1521";
            StatusMessage = "Oracle connection.";

            // Defaults required by spec
            AuthenticationType = PickOrDefault(AuthTypeOptions, "Default");
            ConnectionType = PickOrDefault(ConnectionTypeOptions, "Basic");
            Role = PickOrDefault(RoleOptions, "default");
            UseSid = true;
            UseServiceName = false;
        }
    }

    private static string PickOrDefault(IEnumerable<string> opts, string preferred)
        => opts.FirstOrDefault(o => string.Equals(o, preferred, StringComparison.OrdinalIgnoreCase))
           ?? opts.FirstOrDefault() ?? preferred;

    private ConnectionDefinition BuildDefinition()
    {
        if (!IsSqlSelected && !IsOracleSelected)
            throw new InvalidOperationException("Select Database Type (SQL Server or Oracle).");

        if (!int.TryParse(Port, out var p) || p is < 1 or > 65535)
            throw new InvalidOperationException("Port must be between 1 and 65535.");

        var def = new ConnectionDefinition
        {
            Name = ConnectionName.Trim(),
            Engine = IsSqlSelected ? DatabaseEngine.SqlServer : DatabaseEngine.Oracle,
            Region = SelectedEnvironment,
            Hostname = Hostname.Trim(),
            Port = p,
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            UseWindowsAuthentication = UseWindowsAuth,
            SavePassword = SavePassword,
            DefaultDatabase = string.IsNullOrWhiteSpace(DefaultDatabase) ? null : DefaultDatabase,
            AuthenticationType = IsOracleSelected ? AuthenticationType : null,
            ConnectionType = IsOracleSelected ? ConnectionType : null,
            Role = IsOracleSelected ? Role : null,
            UseSid = IsOracleSelected ? UseSid : true,
            Sid = IsOracleSelected ? (string.IsNullOrWhiteSpace(Sid) ? null : Sid.Trim()) : null,
            ServiceName = IsOracleSelected ? (string.IsNullOrWhiteSpace(ServiceName) ? null : ServiceName.Trim()) : null,
            RuntimePassword = string.IsNullOrWhiteSpace(Password) ? null : Password
        };

        // Validate connection name uniqueness
        var existing = _services.ConnectionStore.LoadAll();
        if (existing.Any(x => string.Equals(x.Name, def.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A connection named '{def.Name}' already exists.");

        def.ValidateForTest();
        return def;
    }

    public async Task TestAsync()
    {
        ErrorDetails = "";
        try
        {
            var def = BuildDefinition();

            var (ok, msg) = await _services.ConnectionManager.TestAsync(def, CancellationToken.None);
            StatusMessage = msg;
            def.LastTestUtc = DateTimeOffset.UtcNow;
            def.LastTestStatus = ok ? ConnectionTestStatus.Green : ConnectionTestStatus.Red;
            def.LastTestMessage = msg;

            if (!ok)
                ErrorDetails = msg;
        }
        catch (Exception ex)
        {
            StatusMessage = "Validation failed.";
            ErrorDetails = ex.Message;
        }
    }

    public async Task RetrieveDatabasesAsync()
    {
        ErrorDetails = "";
        if (!IsSqlSelected)
        {
            StatusMessage = "Retrieve databases is only supported for SQL Server.";
            return;
        }

        try
        {
            var def = BuildDefinition();

            // Use a temporary connection to list databases
            await using var conn = ConnectionStringBuilders.CreateOpenSqlConnection(def);
            var dbs = await _services.SqlMetadata.ListDatabasesAsync(conn, CancellationToken.None);

            DatabaseOptions.Clear();
            foreach (var d in dbs) DatabaseOptions.Add(d);
            StatusMessage = $"Retrieved {dbs.Count} databases.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Retrieve databases failed.";
            ErrorDetails = ex.Message;
        }
    }

    public async Task SaveAsync()
    {
        ErrorDetails = "";
        try
        {
            var def = BuildDefinition();

            // Required by spec: Save should test the connection.
            var (ok, msg) = await _services.ConnectionManager.TestAsync(def, CancellationToken.None);
            def.LastTestUtc = DateTimeOffset.UtcNow;
            def.LastTestStatus = ok ? ConnectionTestStatus.Green : ConnectionTestStatus.Red;
            def.LastTestMessage = msg;

            if (!ok)
            {
                StatusMessage = msg;
                ErrorDetails = msg;
                return;
            }

            // Persist password (encrypted) only if requested
            if (def.Engine == DatabaseEngine.SqlServer && def.UseWindowsAuthentication)
            {
                def.EncryptedPassword = null;
                def.SavePassword = false;
            }
            else if (def.SavePassword)
            {
                def.EncryptedPassword = _services.Protector.ProtectToBase64(def.RuntimePassword ?? "");
            }
            else
            {
                def.EncryptedPassword = null;
            }

            // Never persist runtime password
            def.RuntimePassword = null;

            _services.ConnectionStore.Save(def);
            StatusMessage = "Saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed.";
            ErrorDetails = ex.Message;
        }
    }

    public void Clear()
    {
        ConnectionName = "";
        SelectedEnvironment = Environments.FirstOrDefault() ?? "Dev";
        IsSqlSelected = false;
        IsOracleSelected = false;

        Hostname = "";
        Port = "";
        Username = "";
        Password = "";
        UseWindowsAuth = false;
        SavePassword = true;

        DefaultDatabase = null;
        DatabaseOptions.Clear();

        AuthenticationType = PickOrDefault(AuthTypeOptions, "Default");
        ConnectionType = PickOrDefault(ConnectionTypeOptions, "Basic");
        Role = PickOrDefault(RoleOptions, "default");
        UseSid = true;
        UseServiceName = false;
        Sid = "";
        ServiceName = "";

        StatusMessage = "Cleared.";
        ErrorDetails = "";
    }
}
