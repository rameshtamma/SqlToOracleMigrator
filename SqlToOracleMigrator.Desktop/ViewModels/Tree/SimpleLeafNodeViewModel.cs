using SqlToOracleMigrator.Desktop.ViewModels;

namespace SqlToOracleMigrator.Desktop.ViewModels.Tree;

/// <summary>
/// Leaf node used by the 10-stage pipeline TreeView.
/// Must derive from TreeNodeViewModel so it can live in Children collections.
/// </summary>
public sealed class SimpleLeafNodeViewModel : TreeNodeViewModel
{
    private string? _subtitle;
    private string? _statusText;

    public SimpleLeafNodeViewModel(string displayName, string? subtitle = null)
    {
        DisplayName = displayName;
        _subtitle = subtitle;
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => Set(ref _subtitle, value);
    }

    /// <summary>
    /// Optional status text (e.g., Completed/Running/Failed) shown in UI.
    /// </summary>
    public string? StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }
}
