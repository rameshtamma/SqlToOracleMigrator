using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using SqlToOracleMigrator.Desktop.ViewModels;

namespace SqlToOracleMigrator.Desktop.Views;

public partial class CreatePdbInstanceWindow : Window
{
    private CancellationTokenSource? _loadCts;

    public CreatePdbInstanceWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is CreatePdbInstanceViewModel vm)
            {
                _loadCts?.Cancel();
                _loadCts?.Dispose();
                _loadCts = new CancellationTokenSource();

                await vm.LoadSqlDatabasesAsync(_loadCts.Token);
            }
        }
        catch
        {
            // ignore UI-load exceptions
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
