using SqlToOracleMigrator.Core;

namespace SqlToOracleMigrator.Desktop.Services;

/// <summary>
/// Simple process-wide locator for the desktop app's AppServices instance.
/// This exists to support windows that require services but may be instantiated
/// from code paths where passing AppServices is inconvenient.
/// </summary>
public static class AppServicesLocator
{
    public static AppServices? Current { get; internal set; }
}
