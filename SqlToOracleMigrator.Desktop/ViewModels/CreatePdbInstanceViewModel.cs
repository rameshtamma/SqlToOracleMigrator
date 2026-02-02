using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Oracle.ManagedDataAccess.Client;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.Views;

namespace SqlToOracleMigrator.Desktop.ViewModels;

/// <summary>
/// Creates/ensures an Oracle PDB (from an admin/root connection) and saves a new
/// Oracle connection definition pointing at the PDB service name, so the user can
/// select it in the Connections tree and run migration.
/// </summary>
public sealed class CreatePdbInstanceViewModel : NotifyBase
{
    private readonly AppServices _services;

    private string _selectedOracleAdminConnectionName = "";
    private string _adminUsername = "";
    private bool _connectAsSysDba = true;

    private string _desiredPdbName = "";
    private bool _overrideIfExists = true;
    private string _status = "";
    private string _error = "";

    public ObservableCollection<string> OracleAdminConnectionNames { get; } = new();

    public string SelectedOracleAdminConnectionName
    {
        get => _selectedOracleAdminConnectionName;
        set
        {
            if (Set(ref _selectedOracleAdminConnectionName, value))
            {
                LoadAdminDefaultsFromSelectedConnection();
            }
        }
    }

    /// <summary>
    /// Allows the user to override the saved username from the connection JSON
    /// when creating/dropping a PDB (common when the saved connection is SYSTEM
    /// but PDB admin operations require SYS/SYSDBA).
    /// </summary>
    public string AdminUsername
    {
        get => _adminUsername;
        set => Set(ref _adminUsername, value);
    }

    /// <summary>
    /// Connect using SYSDBA privilege (required for create/drop PDB).
    /// </summary>
    public bool ConnectAsSysDba
    {
        get => _connectAsSysDba;
        set => Set(ref _connectAsSysDba, value);
    }

    public string DesiredPdbName
    {
        get => _desiredPdbName;
        set => Set(ref _desiredPdbName, value);
    }

    public bool OverrideIfExists
    {
        get => _overrideIfExists;
        set => Set(ref _overrideIfExists, value);
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public string Error { get => _error; set => Set(ref _error, value); }

    public string? SavedConnectionName { get; private set; }
    public string? ResolvedPdbName { get; private set; }

    public AsyncRelayCommand CreateCommand { get; }

    public CreatePdbInstanceViewModel(AppServices services, string defaultPdbName)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        DesiredPdbName = string.IsNullOrWhiteSpace(defaultPdbName) ? "AdventureWorks2025" : defaultPdbName;

        // Prefer connected Oracle definitions (must be GREEN).
        var connected = _services.ConnectionManager.GetConnected(DatabaseEngine.Oracle)
            .Where(d => d.LastTestStatus == ConnectionTestStatus.Green)
            .Select(d => d.Name)
            .OrderBy(n => n)
            .ToList();

        foreach (var n in connected) OracleAdminConnectionNames.Add(n);
        SelectedOracleAdminConnectionName = OracleAdminConnectionNames.FirstOrDefault() ?? "";

        CreateCommand = new AsyncRelayCommand(CreateAsync);
        Status = "Select an Oracle root connection, set SYS/SYSDBA credentials, and choose a PDB name.";

        LoadAdminDefaultsFromSelectedConnection();
    }

    private void LoadAdminDefaultsFromSelectedConnection()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SelectedOracleAdminConnectionName)) return;

            var def = _services.ConnectionManager.GetConnected(DatabaseEngine.Oracle)
                .FirstOrDefault(d => d.Name.Equals(SelectedOracleAdminConnectionName, StringComparison.OrdinalIgnoreCase));

            if (def is null) return;

            AdminUsername = (def.Username ?? "").Trim();
            var role = (def.Role ?? "").Trim();
            ConnectAsSysDba = role.Equals("SYSDBA", StringComparison.OrdinalIgnoreCase) || AdminUsername.Equals("SYS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsOracleAdminCredentials(string? username, bool sysdba)
    {
        var u = (username ?? "").Trim();
        if (u.Equals("SYS", StringComparison.OrdinalIgnoreCase)) return true; // SYS must be SYSDBA; enforced below.
        return sysdba; // any user must be SYSDBA to create/drop PDB.
    }

    private async Task EnsurePasswordLoadedAsync(ConnectionDefinition def, bool forcePrompt)
    {
        if (def.Engine == DatabaseEngine.SqlServer && def.UseWindowsAuthentication) return;
        if (!forcePrompt && !string.IsNullOrWhiteSpace(def.RuntimePassword)) return;

        if (!forcePrompt && def.SavePassword && !string.IsNullOrWhiteSpace(def.EncryptedPassword))
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

        var allowEditUser = def.Engine == DatabaseEngine.Oracle || (def.Engine == DatabaseEngine.SqlServer && !def.UseWindowsAuthentication);
        var prompt = new PasswordPromptWindow(def.Name, def.Username ?? string.Empty, allowEditUser) { Owner = Application.Current.MainWindow };
        if (prompt.ShowDialog() != true)
            throw new InvalidOperationException("Password is required.");

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

    private static string NormalizeOracleIdentifierForPdb(string input)
    {
        var raw = input.Trim();
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

        for (var i = 1; i <= 99; i++)
        {
            var candidate = $"{desiredTrim}_V{i}";
            if (!set.Contains(candidate))
                return candidate;
        }
        return $"{desiredTrim}_V{Guid.NewGuid():N}";
    }

    private static ConnectionDefinition Clone(ConnectionDefinition from)
    {
        return new ConnectionDefinition
        {
            Name = from.Name,
            Engine = from.Engine,
            Region = from.Region,
            Notes = from.Notes,
            Color = from.Color,

            Username = from.Username,
            UseWindowsAuthentication = from.UseWindowsAuthentication,
            SavePassword = from.SavePassword,
            EncryptedPassword = from.EncryptedPassword,
            RuntimePassword = from.RuntimePassword,

            Hostname = from.Hostname,
            Port = from.Port,

            DefaultDatabase = from.DefaultDatabase,

            AuthenticationType = from.AuthenticationType,
            ConnectionType = from.ConnectionType,
            Role = from.Role,
            UseSid = from.UseSid,
            Sid = from.Sid,
            ServiceName = from.ServiceName,

            LastTestStatus = from.LastTestStatus,
            LastTestUtc = from.LastTestUtc,
            LastTestMessage = from.LastTestMessage
        };
    }

    private static ConnectionDefinition CreatePdbConnectionDefinition(string connectionName, string pdbName, ConnectionDefinition fromAdmin)
    {
        return new ConnectionDefinition
        {
            Name = connectionName,
            Engine = DatabaseEngine.Oracle,

            Hostname = fromAdmin.Hostname,
            Port = fromAdmin.Port,

            Username = fromAdmin.Username,
            UseWindowsAuthentication = false,
            SavePassword = fromAdmin.SavePassword,
            EncryptedPassword = fromAdmin.EncryptedPassword,
            RuntimePassword = fromAdmin.RuntimePassword,

            AuthenticationType = fromAdmin.AuthenticationType,
            ConnectionType = fromAdmin.ConnectionType,
            Role = fromAdmin.Role,

            UseSid = false,
            Sid = null,
            ServiceName = pdbName,

            Region = fromAdmin.Region,
            Notes = fromAdmin.Notes,
            Color = fromAdmin.Color
        };
    }

    private async Task CreateAsync()
    {
        Error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(SelectedOracleAdminConnectionName))
                throw new InvalidOperationException("Select an Oracle root connection.");

            var connectedDef = _services.ConnectionManager.GetConnected(DatabaseEngine.Oracle)
                .FirstOrDefault(d => d.Name.Equals(SelectedOracleAdminConnectionName, StringComparison.OrdinalIgnoreCase));

            if (connectedDef is null)
                throw new InvalidOperationException("Selected Oracle connection is not connected.");

            if (connectedDef.LastTestStatus != ConnectionTestStatus.Green)
                throw new InvalidOperationException("Selected Oracle connection must be GREEN.");

            var desiredUser = (AdminUsername ?? "").Trim();
            if (string.IsNullOrWhiteSpace(desiredUser))
                throw new InvalidOperationException("Username is required.");

            // If user picks SYS but didn't check SYSDBA, we will enforce SYSDBA.
            if (desiredUser.Equals("SYS", StringComparison.OrdinalIgnoreCase))
                ConnectAsSysDba = true;

            if (!IsOracleAdminCredentials(desiredUser, ConnectAsSysDba))
                throw new InvalidOperationException("Create/drop PDB requires SYSDBA. Set username to SYS and/or check SYSDBA.");

            var adminDef = Clone(connectedDef);
            var originalUser = (adminDef.Username ?? "").Trim();
            var originalRole = (adminDef.Role ?? "").Trim();

            adminDef.Username = desiredUser;
            adminDef.Role = ConnectAsSysDba ? "SYSDBA" : originalRole;

            var userOrRoleChanged = !string.Equals(originalUser, adminDef.Username, StringComparison.OrdinalIgnoreCase)
                                    || !string.Equals(originalRole, adminDef.Role, StringComparison.OrdinalIgnoreCase);

            // If user/role changed, do not try decrypting a saved password for the old user.
            await EnsurePasswordLoadedAsync(adminDef, forcePrompt: userOrRoleChanged);

            var desired = (DesiredPdbName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(desired))
                throw new InvalidOperationException("PDB name is required.");

            Status = "Connecting to Oracle root and checking existing PDBs...";

            await using var open = ConnectionStringBuilders.CreateOpenOracleConnection(adminDef);

            // Check existing PDB names in target system
            var existing = await OraclePdbAdmin.ListPdbsAsync(open, CancellationToken.None);
            var desiredPdb = NormalizeOracleIdentifierForPdb(desired);
            var pdbName = desiredPdb;

            if (!OverrideIfExists)
            {
                var set = new HashSet<string>(existing.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                if (set.Contains(pdbName))
                {
                    for (var i = 1; i <= 99; i++)
                    {
                        var candidate = NormalizeOracleIdentifierForPdb($"{desired}_V{i}");
                        if (!set.Contains(candidate))
                        {
                            pdbName = candidate;
                            break;
                        }
                    }
                }
            }

            Status = OverrideIfExists
                ? $"Creating/recreating PDB '{pdbName}'..."
                : $"Creating PDB '{pdbName}'...";

            // Create/drop/create + switch logic is centralized here (same behavior as migration).
            await OraclePdbProvisioner.EnsureAndSwitchToPdbAsync(open, pdbName, adminPassword: adminDef.RuntimePassword ?? "", dropIfExists: OverrideIfExists, ct: CancellationToken.None);

            ResolvedPdbName = pdbName;

            // Save a new connection definition pointing to the PDB service, so the user can select it for migration.
            var allSaved = _services.ConnectionStore.LoadAll();
            var resolvedConnName = ResolveUniqueName(desired, allSaved.Select(c => c.Name), allowOverwrite: OverrideIfExists);
            var pdbConnDef = CreatePdbConnectionDefinition(resolvedConnName, pdbName, adminDef);
            _services.ConnectionStore.Save(pdbConnDef);

            SavedConnectionName = resolvedConnName;
            Status = $"Created/ensured PDB '{pdbName}'. Saved connection '{resolvedConnName}'.";

            // Close window
            if (Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.DataContext == this) is Window win)
                win.DialogResult = true;
        }
        catch (OracleException ex)
        {
            Status = "Failed.";
            Error = ex.Message;
            _services.Logger.Error("Create PDB failed.", ex);
        }
        catch (Exception ex)
        {
            Status = "Failed.";
            Error = ex.Message;
            _services.Logger.Error("Create PDB failed.", ex);
        }
    }
}
