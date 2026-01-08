using System.Windows;
using System.Windows.Controls;
using SqlToOracleMigrator.Desktop.Services;
using SqlToOracleMigrator.Desktop.ViewModels;

namespace SqlToOracleMigrator.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Replace design-time VM with the bootstrapper-provided VM.
        if (AppServices.Current?.MainViewModel is not null)
            DataContext = AppServices.Current.MainViewModel;
    }

    private async void ConnectionsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not MainViewModel vm) return;
        await vm.OnTreeSelectionChangedAsync(e.NewValue);
    }
}
