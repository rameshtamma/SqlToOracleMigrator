using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SqlToOracleMigrator.Desktop.ViewModels;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Compatibility alias. Some view-models call <c>OnPropertyChanged</c>.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? prop = null)
        => Raise(prop);

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        return true;
    }

    protected void Raise([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
