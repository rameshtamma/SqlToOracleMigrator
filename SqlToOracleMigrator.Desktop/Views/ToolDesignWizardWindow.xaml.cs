using System.Windows;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.ViewModels;

namespace SqlToOracleMigrator.Desktop.Views;

public partial class ToolDesignWizardWindow : Window
{
    private readonly ToolDesignWizardViewModel _vm;

    public ToolDesignWizardWindow(AppServices services, ConnectionDefinition sourceSql, string sourceDatabase)
    {
        InitializeComponent();
        _vm = new ToolDesignWizardViewModel(services, sourceSql, sourceDatabase);
        DataContext = _vm;

        _vm.RequestClose += ok =>
        {
            DialogResult = ok;
            Close();
        };
    }
}
