using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Management;
using System.Security.Principal;

namespace LGSTrayUI;

public static class CheckTheme
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string RegistryValueName = "SystemUsesLightTheme";

    private static bool _lightTheme = true;
    public static bool LightTheme => _lightTheme;

    public static string ThemeSuffix
    {
        get
        {
            return LightTheme ? "" : "_dark";
        }
    }

    public static event PropertyChangedEventHandler? StaticPropertyChanged;

    static CheckTheme()
    {
        var currentUser = WindowsIdentity.GetCurrent();
        string query = string.Format(
            CultureInfo.InvariantCulture,
            @"SELECT * FROM RegistryValueChangeEvent WHERE Hive = 'HKEY_USERS' AND KeyPath = '{0}\\{1}' AND ValueName = '{2}'",
            currentUser.User!.Value,
            RegistryKeyPath.Replace(@"\", @"\\"),
            RegistryValueName);

        try
        {
            var watcher = new ManagementEventWatcher(query);
            watcher.EventArrived += Watcher_EventArrived;

            watcher.Start();
            UpdateThemeStatus();
        }
        catch (Exception ex)
        {
            // WMI watcher may fail on Windows 10 due to permissions or WMI issues
            // Default to light theme (more common)
            _lightTheme = true;
            LGSTrayPrimitives.DiagnosticLogger.LogWarning($"Failed to initialize theme watcher, defaulting to light theme: {ex.Message}");

            // Still try to read current theme from registry even if watcher fails
            try
            {
                UpdateThemeStatus();
            }
            catch (Exception regEx)
            {
                LGSTrayPrimitives.DiagnosticLogger.LogWarning($"Failed to read theme from registry: {regEx.Message}");
            }
        }

    }

    private static void UpdateThemeStatus()
    {
        try
        {
            var regPath = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            if (regPath == null)
            {
                LGSTrayPrimitives.DiagnosticLogger.LogWarning($"Registry key not found: {RegistryKeyPath}, defaulting to light theme");
                _lightTheme = true;
                return;
            }

            var regValue = regPath.GetValue(RegistryValueName, 1); // Default to 1 (light)
            int regFlag = regValue is int intValue ? intValue : 1;

            _lightTheme = regFlag != 0;
            StaticPropertyChanged?.Invoke(typeof(CheckTheme), new(nameof(LightTheme)));
        }
        catch (Exception ex)
        {
            LGSTrayPrimitives.DiagnosticLogger.LogWarning($"Error reading theme registry value: {ex.Message}");
            _lightTheme = true;
        }
    }

    private static void Watcher_EventArrived(object sender, EventArrivedEventArgs e)
    {
        UpdateThemeStatus();
    }
}
