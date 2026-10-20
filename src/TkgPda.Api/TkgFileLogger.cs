using System.Collections.Concurrent;
using System.Text;

namespace TkgPda.Api;

/// <summary>
/// A lightweight, resilient rolling file logger provider for TkgPda.Api.
/// Writes timestamped log entries to C:\ProgramData\TKG PDA\Logs\server_{date}.log
/// or fallback local logs\server_{date}.log.
/// Thread-safe, non-blocking with immediate flush on Warning/Error/Critical.
/// </summary>
public sealed class TkgFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly ConcurrentDictionary<string, TkgFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();
    private bool _disposed;

    public TkgFileLoggerProvider(string? customLogDirectory = null)
    {
        _logDirectory = ResolveLogDirectory(customLogDirectory);
        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }
        }
        catch
        {
            _logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(_logDirectory);
        }
    }

    public string LogDirectory => _logDirectory;

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new TkgFileLogger(name, this));
    }

    internal void WriteLog(string category, LogLevel level, string message, Exception? exception)
    {
        if (_disposed) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var levelStr = level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => "INFO "
        };

        var sb = new StringBuilder(256);
        sb.Append('[').Append(timestamp).Append("] [").Append(levelStr).Append("] [").Append(category).Append("] ");
        sb.Append(message);

        if (exception != null)
        {
            sb.AppendLine();
            sb.Append("Exception: ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
            sb.AppendLine();
            sb.Append(exception.StackTrace);
        }

        var line = sb.ToString();

        lock (_writeLock)
        {
            try
            {
                var fileName = $"server_{DateTime.Now:yyyyMMdd}.log";
                var fullPath = Path.Combine(_logDirectory, fileName);
                using var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
                writer.Flush();
            }
            catch
            {
                // File logging should never crash the caller
            }
        }
    }

    private static string ResolveLogDirectory(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        try
        {
            var envProgramData = Environment.GetEnvironmentVariable("TKG_PROGRAM_DATA");
            if (!string.IsNullOrWhiteSpace(envProgramData))
            {
                return Path.Combine(envProgramData, "Logs");
            }

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
            {
                var demoDir = Path.Combine(programData, "TKG PDA Demo", "Logs");
                if (Directory.Exists(Path.Combine(programData, "TKG PDA Demo")))
                {
                    return demoDir;
                }
                return Path.Combine(programData, "TKG PDA", "Logs");
            }
        }
        catch
        {
            // Ignore
        }

        return Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public void Dispose()
    {
        _disposed = true;
        _loggers.Clear();
    }
}

public sealed class TkgFileLogger(string category, TkgFileLoggerProvider provider) : ILogger
{
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
        var message = formatter(state, exception);
        provider.WriteLog(category, logLevel, message, exception);
    }
}
