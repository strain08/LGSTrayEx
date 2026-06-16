using System.Runtime.CompilerServices;

namespace LGSTrayPrimitives;

/// <summary>
/// Diagnostic logger for tracing device discovery and UI updates.
/// Writes to <c>%LOCALAPPDATA%\LGSTray\diagnostic.log</c> 
/// Enable via the [Logging] config section or the --log command-line flag.
/// </summary>
public static class DiagnosticLogger
{
    private static readonly string _logFilePath = Path.Combine(AppDataPaths.LocalAppDataDir, "diagnostic.log");

    private static bool _isEnabled = false;
    private static bool _isVerboseEnabled = false;
    private static int _maxLines = 1000;
    private static int _writesSinceTrim = 0;

    // Set once when a write fails so we surface the problem to the user a single time,
    // rather than silently swallowing every failed append.
    private static int _writeFailureReported = 0;

    /// <summary>
    /// Number of writes between trim checks. Avoids checking file size on every write.
    /// </summary>
    private const int TrimCheckInterval = 100;

    /// <summary>
    /// Full path of the diagnostic log file. Lives in <see cref="AppDataPaths.LocalAppDataDir"/>
    /// (<c>%LOCALAPPDATA%\LGSTray\diagnostic.log</c>), which is user-writable and survives
    /// installs to UAC-protected folders such as Program Files.
    /// </summary>
    public static string LogFilePath => _logFilePath;

    /// <summary>
    /// The error message from the most recent failed log write, or null if none.
    /// </summary>
    public static string? LastWriteError { get; private set; }

    /// <summary>
    /// Raised the first time a diagnostic log write fails (e.g. the target folder is
    /// read-only). Fired at most once per process so subscribers (UI) can surface a single
    /// notification instead of spamming. The argument is a human-readable description
    /// including the offending path.
    /// </summary>
    public static event Action<string>? WriteFailed;

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

            // Recovered: allow a future failure to be reported again.
            Interlocked.Exchange(ref _writeFailureReported, 0);
        }
        catch (Exception ex)
        {
            LastWriteError = ex.Message;
            Console.WriteLine($"Failed to write to diagnostic log file '{_logFilePath}': {ex.Message}");

            // Surface to subscribers only on the first failure of a streak to avoid flooding.
            if (Interlocked.Exchange(ref _writeFailureReported, 1) == 0)
            {
                try
                {
                    WriteFailed?.Invoke(
                        $"Diagnostic logging is enabled but writing to '{_logFilePath}' failed: {ex.Message}");
                }
                catch (Exception)
                {
                    // Never let a faulty subscriber break logging.
                }
            }
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
