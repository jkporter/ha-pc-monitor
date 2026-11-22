using Microsoft.Extensions.Logging;

namespace HassHwStats.Core;

/// <summary>Minimal file logger so the app can run hidden (scheduled task) and still leave a trail. Keeps one previous file.</summary>
public sealed class FileLoggerProvider(string path, LogLevel minLevel) : ILoggerProvider
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    internal LogLevel MinLevel { get; } = minLevel;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName.Split('.').Last());

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the service down.
            }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.MinLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel.ToString()[..4].ToUpperInvariant()}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Write(line);
        }
    }
}
