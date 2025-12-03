using System.Diagnostics;

namespace LGSTrayPrimitives;

/// <summary>
/// Diagnostic logger for tracing device discovery and UI updates.
/// Writes to diagnostic.log file in the application directory.
/// Works in both Debug and Release builds.
/// </summary>
public static class DiagnosticLogger
{
    private static readonly object _lock = new object();
    private static readonly string _logFilePath = Path.Combine(AppContext.BaseDirectory, "diagnostic.log");

    /// <summary>
    /// Log an informational message with timestamp.
    /// </summary>
    public static void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] LGSTray: {message}";
        WriteToFile(formatted);
        Debug.WriteLine(formatted);
    }

    /// <summary>
    /// Log a warning message with timestamp.
    /// </summary>
    public static void LogWarning(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] LGSTray WARNING: {message}";
        WriteToFile(formatted);
        Debug.WriteLine(formatted);
    }

    /// <summary>
    /// Log an error message with timestamp.
    /// </summary>
    public static void LogError(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] LGSTray ERROR: {message}";
        WriteToFile(formatted);
        Debug.WriteLine(formatted);
    }

    private static void WriteToFile(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_logFilePath, message + Environment.NewLine);
            }
        }
        catch
        {
            // Silently fail if we can't write to log file
        }
    }
}
