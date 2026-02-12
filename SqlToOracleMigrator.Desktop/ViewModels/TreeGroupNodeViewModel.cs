namespace SqlToOracleMigrator.Desktop.ViewModels;

/// <summary>
/// Group/header node used by the 10-stage pipeline TreeView (phase/category grouping).
/// </summary>
public sealed class TreeGroupNodeViewModel : TreeNodeViewModel
{
    public TreeGroupNodeViewModel(string displayName)
    {
        DisplayName = displayName;
    }
}
