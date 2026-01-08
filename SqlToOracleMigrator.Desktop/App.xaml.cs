using System.Windows;
using SqlToOracleMigrator.Core;
using SqlToOracleMigrator.Desktop.Services;

namespace SqlToOracleMigrator.Desktop;

public partial class App : Application
{
    private AppBootstrapper? _bootstrapper;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _bootstrapper = new AppBootstrapper();
        _bootstrapper.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _bootstrapper?.Dispose();
        }
        catch
        {
            // never throw on exit
        }
        base.OnExit(e);
    }
}
