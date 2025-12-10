using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LGSTrayPrimitives;

/// <summary>
/// Diagnostic logger for tracing device discovery and UI updates.
/// Writes to diagnostic.log file in the application directory.
/// Calls to log fuctions are discarded in Release builds
/// </summary>
public static class DiagnosticLogger
{
    private static readonly object _lock = new object();
    private static readonly string _logFilePath = Path.Combine(AppContext.BaseDirectory, "diagnostic.log");

    /// <summary>
    /// Log an informational message with timestamp.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Log(string message, [CallerMemberName] string callerMember = "")
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] [{callerMember}]: {message}";
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }
    /// <summary>
    /// Log verbose message with timestamp.
    /// Requires VERBOSE compilation symbol.
    /// </summary>
    [Conditional("VERBOSE")]
    public static void Verbose(string message, [CallerMemberName] string callerMember = "")
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] [{callerMember}]: {message}";
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }



    /// <summary>
    /// Log a warning message with timestamp.
    /// </summary>
    [Conditional("DEBUG")]
    public static void LogWarning(string message, [CallerMemberName] string callerMember = "")
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] LGSTray WARNING: {message}";
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }

    /// <summary>
    /// Log an error message with timestamp.
    /// </summary>
    [Conditional("DEBUG")]
    public static void LogError(string message, [CallerMemberName] string callerMember = "")
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] LGSTray ERROR: {message}";
        WriteToFile(formatted);
        WriteToConsole(formatted);
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
            Console.WriteLine("Failed to write to diagnostic log file.");
        }
    }
    
    [Conditional("DEBUG")]
    private static void WriteToConsole(string formatted)
    {

        Console.WriteLine(formatted);
    }


    public static void ResetLog()
    {    
        File.WriteAllText(_logFilePath, string.Empty);
    }
}
