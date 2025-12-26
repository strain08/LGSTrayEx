using System;

namespace LGSTrayUI;

/// <summary>
/// Provides Windows version detection for feature compatibility checks.
/// </summary>
public static class WindowsVersionHelper
{
    // Windows 11 is build 22000+
    private const int Windows11BuildNumber = 22000;

    /// <summary>
    /// Gets whether the current OS is Windows 11 or later.
    /// </summary>
    public static bool IsWindows11OrGreater { get; } = DetectWindows11();

    /// <summary>
    /// Gets whether the current OS is Windows 10 (but not Windows 11).
    /// </summary>
    public static bool IsWindows10 { get; } = OperatingSystem.IsWindowsVersionAtLeast(10) && !IsWindows11OrGreater;

    private static bool DetectWindows11()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return false;

        try
        {
            // Windows 11 = Windows 10 build 22000+
            var version = Environment.OSVersion.Version;
            return version.Build >= Windows11BuildNumber;
        }
        catch
        {
            // Fallback: assume Windows 10 if detection fails
            return false;
        }
    }
}
