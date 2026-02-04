using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private void InventoryGrid_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel) return;
        if (sender is not DataGrid grid) return;

        // Only toggle when the user double-clicks a row in the *outer* inventory grid.
        var dep = e.OriginalSource as DependencyObject;
        var row = FindVisualParent<DataGridRow>(dep);
        if (row?.Item is not InventoryDbRowViewModel item) return;

        item.IsExpanded = !item.IsExpanded;
        e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
