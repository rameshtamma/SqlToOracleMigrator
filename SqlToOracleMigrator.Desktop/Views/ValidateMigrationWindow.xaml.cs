using System.Windows;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.ViewModels;

namespace SqlToOracleMigrator.Desktop.Views;

public partial class ValidateMigrationWindow : Window
{
    public ValidateMigrationWindow(AppServices services)
    {
        InitializeComponent();
        if (DataContext is ValidateMigrationViewModel vm)
        {
            vm.Initialize(services);
        }
    }
}
