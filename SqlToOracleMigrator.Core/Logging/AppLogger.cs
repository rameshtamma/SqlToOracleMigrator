using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace SqlToOracleMigrator.Core;

public interface IAppLogger
{
    event EventHandler<LogEntry>? EntryWritten;

    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);

    string CurrentRunLogFilePath { get; }
}

public sealed class FileAppLogger : IAppLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly AppPaths _paths;
    private readonly string _runFilePath;
    private bool _disposed;

    public event EventHandler<LogEntry>? EntryWritten;

    public string CurrentRunLogFilePath => _runFilePath;

    public FileAppLogger(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _paths.EnsureCreated();

        var dayFolder = Path.Combine(_paths.LogsDirectory, DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dayFolder);

        var runName = $"run_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        _runFilePath = Path.Combine(dayFolder, runName);

        // Write header
        Info("=== SqlToOracleMigrator run started ===");
    }

    public void Info(string message) => Write(AppLogLevel.Info, message, null);
    public void Warn(string message) => Write(AppLogLevel.Warn, message, null);
    public void Error(string message, Exception? ex = null) => Write(AppLogLevel.Error, message, ex);

    private void Write(AppLogLevel level, string message, Exception? ex)
    {
        if (_disposed) return;

        var entry = new LogEntry(DateTimeOffset.Now, level, message, ex?.ToString());
        var line = $"{entry.Timestamp:O} [{entry.Level}] {entry.Message}{(entry.Detail is null ? "" : Environment.NewLine + entry.Detail)}";

        lock (_gate)
        {
            File.AppendAllText(_runFilePath, line + Environment.NewLine, Encoding.UTF8);
        }

        EntryWritten?.Invoke(this, entry);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Info("=== SqlToOracleMigrator run ended ===");
    }
}

public sealed class InMemoryAppLogger : IAppLogger
{
    private readonly int _maxEntries;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    public event EventHandler<LogEntry>? EntryWritten;
    public string CurrentRunLogFilePath => "";

    public InMemoryAppLogger(int maxEntries = 5000)
    {
        _maxEntries = Math.Max(100, maxEntries);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        return _entries.ToArray();
    }

    public void Info(string message) => Write(AppLogLevel.Info, message, null);
    public void Warn(string message) => Write(AppLogLevel.Warn, message, null);
    public void Error(string message, Exception? ex = null) => Write(AppLogLevel.Error, message, ex);

    private void Write(AppLogLevel level, string message, Exception? ex)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message, ex?.ToString());
        _entries.Enqueue(entry);

        while (_entries.Count > _maxEntries && _entries.TryDequeue(out _)) { }

        EntryWritten?.Invoke(this, entry);
    }
}

public sealed class CompositeLogger : IAppLogger
{
    private readonly IAppLogger[] _loggers;
    public event EventHandler<LogEntry>? EntryWritten;
    public string CurrentRunLogFilePath => _loggers.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.CurrentRunLogFilePath))?.CurrentRunLogFilePath ?? "";

    public CompositeLogger(params IAppLogger[] loggers)
    {
        _loggers = loggers ?? Array.Empty<IAppLogger>();
        foreach (var l in _loggers)
        {
            l.EntryWritten += (s, e) => EntryWritten?.Invoke(this, e);
        }
    }

    public void Info(string message) { foreach (var l in _loggers) l.Info(message); }
    public void Warn(string message) { foreach (var l in _loggers) l.Warn(message); }
    public void Error(string message, Exception? ex = null) { foreach (var l in _loggers) l.Error(message, ex); }
}
