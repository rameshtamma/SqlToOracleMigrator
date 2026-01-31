using System.Collections.ObjectModel;
using System.Windows;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Desktop.Services;

namespace SqlToOracleMigrator.Desktop.ViewModels;

public sealed class PdbManagerViewModel : NotifyBase
{
    private readonly AppServices _services;
    private readonly string _oracleConnectionName;

    private string _status = "";
    private string _error = "";

    public ObservableCollection<PdbRow> Pdbs { get; } = new();

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand DeleteSelectedCommand { get; }

    public PdbManagerViewModel(AppServices services, string oracleConnectionName)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _oracleConnectionName = oracleConnectionName ?? throw new ArgumentNullException(nameof(oracleConnectionName));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync);

        _ = RefreshAsync();
    }

    private Oracle.ManagedDataAccess.Client.OracleConnection GetOpenOracle()
    {
        var open = _services.ConnectionManager.TryGetOpenOracle(_oracleConnectionName);
        if (open is null)
            throw new InvalidOperationException("Oracle connection is not active. Connect first.");
        return open;
    }

    private async Task RefreshAsync()
    {
        Error = "";
        try
        {
            Status = "Loading PDBs...";
            Pdbs.Clear();

            var open = GetOpenOracle();
            var list = await OraclePdbAdmin.ListPdbsAsync(open, CancellationToken.None);
            foreach (var p in list)
            {
                var notes = OraclePdbAdmin.IsProtectedPdbName(p.Name)
                    ? "Protected"
                    : "";
                Pdbs.Add(new PdbRow(p.Name, p.OpenMode, p.Restricted, notes));
            }

            Status = $"Loaded {Pdbs.Count} PDB(s).";
            // no-op; button always enabled and validates selection at runtime
        }
        catch (Exception ex)
        {
            Status = "Failed to load PDBs.";
            Error = ex.Message;
        }
    }

    private async Task DeleteSelectedAsync()
    {
        Error = "";
        try
        {
            var selected = Pdbs.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0)
                return;

            var msg = "This will DROP the selected PDB(s) INCLUDING DATAFILES.\n\n" +
                      string.Join("\n", selected.Select(s => "• " + s.Name)) +
                      "\n\nContinue?";

            if (MessageBox.Show(msg, "Confirm PDB Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            Status = "Deleting selected PDBs...";
            var open = GetOpenOracle();

            foreach (var p in selected)
            {
                if (OraclePdbAdmin.IsProtectedPdbName(p.Name))
                    continue;

                await OraclePdbAdmin.DropPdbAsync(open, p.Name, includingDatafiles: true, CancellationToken.None);
            }

            await RefreshAsync();
            Status = "Delete complete.";
        }
        catch (Exception ex)
        {
            Status = "Delete failed.";
            Error = ex.Message;
        }
    }

    public sealed class PdbRow : NotifyBase
    {
        private bool _isSelected;

        public PdbRow(string name, string openMode, string restricted, string notes)
        {
            Name = name;
            OpenMode = openMode;
            Restricted = restricted;
            Notes = notes;
        }

        public string Name { get; }
        public string OpenMode { get; }
        public string Restricted { get; }
        public string Notes { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }
    }
}
