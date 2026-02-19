using System;
using System.Windows;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.ViewModels;

namespace SqlToOracleMigrator.Desktop.Views;

public partial class ValidateMigrationWindow : Window
{
    /// <summary>
    /// Parameterless constructor for XAML/designer and for callers that do not
    /// have AppServices available at construction time.
    /// </summary>
    public ValidateMigrationWindow() : this(AppServicesLocator.Current
        ?? throw new InvalidOperationException("AppServices not initialized. Ensure AppBootstrapper.Initialize() has run."))
    {
    }

    public ValidateMigrationWindow(AppServices services)
    {
        InitializeComponent();
        if (DataContext is ValidateMigrationViewModel vm)
        {
            vm.Initialize(services);
        }
    }
}
