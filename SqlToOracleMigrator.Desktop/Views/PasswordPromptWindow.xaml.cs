using System.Windows;

namespace SqlToOracleMigrator.Desktop.Views;

public partial class PasswordPromptWindow : Window
{
    public string PromptText { get; }
    public string Password { get; private set; } = "";

    public PasswordPromptWindow(string connectionName)
    {
        InitializeComponent();
        PromptText = $"Enter password for '{connectionName}':";
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
}
