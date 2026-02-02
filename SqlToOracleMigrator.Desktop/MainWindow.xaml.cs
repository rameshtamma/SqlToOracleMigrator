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

    private async void ConnectionsTree_PreviewMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not TreeView tree) return;

        // Use the currently selected item so double-click works even if selection doesn't change.
        await vm.OnTreeItemDoubleClickedAsync(tree.SelectedItem);
    }
}
