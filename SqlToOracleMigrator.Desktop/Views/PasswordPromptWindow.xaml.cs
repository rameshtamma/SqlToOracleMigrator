using System.ComponentModel;
using System.Runtime.CompilerServices;
using System;
using System.Windows;

namespace SqlToOracleMigrator.Desktop.Views;

/// <summary>
/// Simple credential prompt used when a connection requires a runtime password.
/// Supports optional UserId editing (primarily for Oracle SYS/SYSDBA use cases,
/// but can also be used for SQL authentication).
/// </summary>
public partial class PasswordPromptWindow : Window, INotifyPropertyChanged
{
    private string _userId = "";
    private bool _allowUserIdEdit;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PromptText { get; private set; } = "Enter credentials:";

    /// <summary>
    /// Bound to the User Id text box. Must be settable for TwoWay binding.
    /// </summary>
    public string UserId
    {
        get => _userId;
        set
        {
            if (string.Equals(_userId, value, StringComparison.Ordinal)) return;
            _userId = value;
            OnPropertyChanged();
        }
    }

    public bool AllowUserIdEdit
    {
        get => _allowUserIdEdit;
        set
        {
            if (_allowUserIdEdit == value) return;
            _allowUserIdEdit = value;
            OnPropertyChanged();
        }
    }

    public string Password { get; private set; } = "";

    // Backwards-compatible constructor: previous code used a 1-arg overload.
    public PasswordPromptWindow(string connectionName)
        : this(connectionName, userId: string.Empty, allowUserIdEdit: false)
    {
    }

    public PasswordPromptWindow(string connectionName, string userId, bool allowUserIdEdit)
    {
        InitializeComponent();

        PromptText = $"Enter credentials for '{connectionName}':";
        UserId = userId ?? string.Empty;
        AllowUserIdEdit = allowUserIdEdit;

        DataContext = this;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = Pwd.Password;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
