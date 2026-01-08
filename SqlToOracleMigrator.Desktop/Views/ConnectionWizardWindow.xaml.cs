using System.Windows;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.ViewModels;

namespace SqlToOracleMigrator.Desktop.Views;

public partial class ConnectionWizardWindow : Window
{
    private readonly ConnectionWizardViewModel _vm;

    public ConnectionWizardWindow(AppServices services)
    {
        InitializeComponent();
        _vm = new ConnectionWizardViewModel(services);
        DataContext = _vm;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        _vm.Password = _vm.IsSqlSelected ? SqlPwd.Password : OraPwd.Password;
        await _vm.TestAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.Password = _vm.IsSqlSelected ? SqlPwd.Password : OraPwd.Password;
        await _vm.SaveAsync();

        if (string.Equals(_vm.StatusMessage, "Saved.", StringComparison.OrdinalIgnoreCase))
        {
            DialogResult = true;
            Close();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SqlPwd.Password = string.Empty;
        OraPwd.Password = string.Empty;
        _vm.Clear();
    }

    private async void Retrieve_Click(object sender, RoutedEventArgs e)
    {
        _vm.Password = _vm.IsSqlSelected ? SqlPwd.Password : OraPwd.Password;
        await _vm.RetrieveDatabasesAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
