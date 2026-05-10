using Microsoft.Extensions.Logging;

namespace BetterXeneonWidget.Host.Diagnostics;

/// <summary>
/// Minimal append-only file logger. Writes a single line per log event so we
/// can diagnose silent-launch failures (the host runs via wscript with no
/// visible console — without a file, every error is invisible).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _writeLock = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Roll the log when it crosses 1 MB so it doesn't grow unbounded
            // across long uptime. Keep one previous as .log.old.
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
            {
                var old = path + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
        }
        catch
        {
            /* best-effort — logging must never throw */
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    internal void Append(string line)
    {
        lock (_writeLock)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch { /* logging must never throw */ }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var msg = formatter(state, exception);
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???",
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {_category}: {msg}";
            if (exception != null)
                line += Environment.NewLine + exception;
            _provider.Append(line);
        }
    }
}
