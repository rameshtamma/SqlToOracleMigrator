using System.Reflection;

namespace SqlToOracleMigrator.Core;

public sealed class AppPaths
{
    public string AppBaseDirectory { get; }
    public string DataDirectory { get; }
    public string ConfigDirectory { get; }
    public string ConnectionsDirectory { get; }
    public string LogsDirectory { get; }
    public string TemplatesDirectory { get; }

    public AppPaths(string? baseDirectory = null)
    {
        AppBaseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        DataDirectory = Path.Combine(AppBaseDirectory, "Data");
        ConfigDirectory = Path.Combine(DataDirectory, "Config");
        ConnectionsDirectory = Path.Combine(DataDirectory, "Connections");
        LogsDirectory = Path.Combine(DataDirectory, "Logs");
        TemplatesDirectory = Path.Combine(DataDirectory, "Templates");
    }


    public string GetRunDirectory(Guid runId, DateTimeOffset? startedLocal = null)
    {
        var day = (startedLocal ?? DateTimeOffset.Now).ToString("yyyyMMdd");
        return Path.Combine(LogsDirectory, day, runId.ToString("N"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ConnectionsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TemplatesDirectory);
    }
}
