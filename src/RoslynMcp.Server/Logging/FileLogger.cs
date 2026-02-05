namespace RoslynMcp.Server.Logging;

/// <summary>
/// Simple file-based logger for diagnostics.
/// Writes to a log file in the user's temp directory with thread-safe operations.
/// </summary>
public static class FileLogger
{
    private static readonly object _lock = new();
    private static readonly string _logDirectory;
    private static readonly string _logFilePath;
    private static bool _initialized;

    static FileLogger()
    {
        _logDirectory = Path.Combine(Path.GetTempPath(), "RoslynMcp");
        _logFilePath = Path.Combine(_logDirectory, "server.log");
    }

    /// <summary>
    /// Gets the path to the log file.
    /// </summary>
    public static string LogFilePath => _logFilePath;

    /// <summary>
    /// Logs an informational message with timestamp.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public static void Log(string message)
    {
        WriteEntry("INFO", message);
    }

    /// <summary>
    /// Logs an error message with optional exception details.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="ex">Optional exception to include.</param>
    public static void LogError(string message, Exception? ex = null)
    {
        var fullMessage = ex != null
            ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}"
            : message;

        WriteEntry("ERROR", fullMessage);

        if (ex?.StackTrace != null)
        {
            WriteEntry("ERROR", $"StackTrace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Logs a warning message with timestamp.
    /// </summary>
    /// <param name="message">The warning message.</param>
    public static void LogWarning(string message)
    {
        WriteEntry("WARN", message);
    }

    private static void WriteEntry(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                EnsureInitialized();

                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logLine = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";

                File.AppendAllText(_logFilePath, logLine);
            }
        }
        catch
        {
            // Silently ignore logging failures to avoid disrupting server operation
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }

        _initialized = true;
    }
}
