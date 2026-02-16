using System.Runtime.CompilerServices;

namespace LGSTrayPrimitives;

/// <summary>
/// Diagnostic logger for tracing device discovery and UI updates.
/// Writes to diagnostic.log file in the application directory.
/// Enable with --log command-line flag. Works in both Debug and Release builds.
/// </summary>
public static class DiagnosticLogger
{
    private static readonly string _logFilePath = Path.Combine(AppContext.BaseDirectory, "diagnostic.log");

    private static bool _isEnabled = false;
    private static bool _isVerboseEnabled = false;
    private static int _maxLines = 1000;
    private static int _writesSinceTrim = 0;

    /// <summary>
    /// Number of writes between trim checks. Avoids checking file size on every write.
    /// </summary>
    private const int TrimCheckInterval = 100;

    /// <summary>
    /// Gets whether logging is enabled (--log flag).
    /// </summary>
    public static bool IsEnabled => _isEnabled;

    /// <summary>
    /// Gets whether verbose logging is enabled (--verbose flag).
    /// </summary>
    public static bool IsVerboseEnabled => _isVerboseEnabled;

    /// <summary>
    /// Initialize logging based on command-line arguments.
    /// Must be called before any Log() calls.
    /// </summary>
    /// <param name="enableLogging">Enable standard logging (--log)</param>
    /// <param name="enableVerbose">Enable verbose logging (--verbose)</param>
    /// <param name="maxLines">Maximum lines to keep in log file (0 = unlimited)</param>
    public static void Initialize(bool enableLogging, bool enableVerbose, int maxLines = 1000)
    {
        _isEnabled = enableLogging;
        _isVerboseEnabled = enableVerbose;
        _maxLines = maxLines;
        _writesSinceTrim = 0;

        // If verbose is enabled, standard logging must also be enabled
        if (_isVerboseEnabled && !_isEnabled)
        {
            _isEnabled = true;
        }

        // Trim log file at startup if it exceeds the limit
        if (_isEnabled && _maxLines > 0)
        {
            try
            {
                using var mutex = new Mutex(false, "LOG_WRITE");
                if (mutex.WaitOne(Timeout.Infinite, false))
                {
                    try { TrimLogFile(); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception)
            {
                // Silently ignore trim failures at startup
            }
        }
    }

    /// <summary>
    /// Log an informational message with timestamp.
    /// </summary>
    public static void Log(string message, [CallerMemberName] string callerMember = "")
    {
        if (!_isEnabled) return;

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] [{callerMember}]: {message}";
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }
    /// <summary>
    /// Log verbose message with timestamp.
    /// Requires --verbose flag.
    /// </summary>
    public static void Verbose(string message, [CallerMemberName] string callerMember = "")
    {
        if (!_isVerboseEnabled) return;

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] [VERBOSE] [{callerMember}]: {message}";
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }



    /// <summary>
    /// Log a warning message with timestamp.
    /// </summary>
    public static void LogWarning(string message, [CallerMemberName] string callerMember = "")
    {
        if (!_isEnabled) return;

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] WARNING: {message}";
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }

    /// <summary>
    /// Log an error message with timestamp.
    /// </summary>
    public static void LogError(string message, [CallerMemberName] string callerMember = "")
    {
        if (!_isEnabled) return;

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{timestamp}] ERROR: {message}";
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }

    private static void WriteToFile(string message)
    {
        using var mutex = new Mutex(false, "LOG_WRITE");
        var hasHandle = false;
        try
        {
            hasHandle = mutex.WaitOne(Timeout.Infinite, false);

            File.AppendAllText(_logFilePath, message + Environment.NewLine);

            _writesSinceTrim++;
            if (_maxLines > 0 && _writesSinceTrim >= TrimCheckInterval)
            {
                _writesSinceTrim = 0;
                TrimLogFile();
            }
        }
        catch (Exception)
        {
            Console.WriteLine("Failed to write to diagnostic log file.");
        }
        finally
        {
            if (hasHandle)
                mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Trims the log file to MaxLines if it exceeds the limit.
    /// Must be called while holding the LOG_WRITE mutex.
    /// </summary>
    private static void TrimLogFile()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;

            var lines = File.ReadAllLines(_logFilePath);
            if (lines.Length <= _maxLines) return;

            // Keep the last _maxLines lines
            var trimmed = lines[^_maxLines..];
            File.WriteAllLines(_logFilePath, trimmed);
        }
        catch { }
    }

    private static void WriteToConsole(string formatted)
    {
#if DEBUG
        Console.WriteLine(formatted);
#endif
    }


}
