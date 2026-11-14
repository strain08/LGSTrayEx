using Hardcodet.Wpf.TaskbarNotification.Interop;
using LGSTrayPrimitives;
using Microsoft.Win32;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace LGSTrayUI.IconDrawing;

/// <summary>
/// Tracks the DPI of the taskbar (the scale tray icons are displayed at) and raises
/// <see cref="Changed"/> when it changes while the app is running.
/// </summary>
/// <remarks>Requires the process to be per-monitor DPI aware (see app.manifest); a system-aware
/// process is handed the DPI captured at startup and never sees a change.
/// <br>The event may be raised on a non-UI thread; subscribers must marshal to the Dispatcher.</br></remarks>
public static partial class TaskbarDpi
{
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    private static uint _lastDpi;

    /// <summary>
    /// Raised when the taskbar DPI differs from the last observed value.
    /// </summary>
    public static event Action? Changed;

    static TaskbarDpi()
    {
        _lastDpi = Current;

        // A scale change arrives as WM_DISPLAYCHANGE and WM_SETTINGCHANGE; listen to both and only
        // fire when the DPI actually moved. On the first change after startup these events arrive
        // before Explorer applies the new DPI to the taskbar, which OnSettled catches.
        SystemEvents.DisplaySettingsChanged += (_, _) => OnSystemEvent("DisplaySettingsChanged");
        SystemEvents.UserPreferenceChanged += (_, e) => OnSystemEvent($"UserPreferenceChanged({e.Category})");

        DiagnosticLogger.Log($"[TaskbarDpi] Initialized, taskbar dpi {_lastDpi}");
    }

    /// <summary>
    /// Current DPI of the taskbar window, falling back to the system DPI if it can't be resolved.
    /// </summary>
    public static uint Current
    {
        get
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(taskbar);
                if (dpi != 0)
                {
                    return dpi;
                }
            }

            return GetDpiForSystem();
        }
    }

    /// <summary>
    /// Current taskbar scale factor (1.0 = 100%).
    /// </summary>
    public static float Scale => Current / 96.0f;

    // Settle window after the last display/settings event: catches a DPI that Explorer applies
    // late (seen on the first change after startup) and repaints once Explorer has finished
    // rebuilding the notification area, in case the immediate redraw landed mid-rebuild.
    private const int SettleDelayMs = 1500;
    private static readonly object _lock = new();
    private static Timer? _settleTimer;
    private static bool _changedInBurst;

    private static void OnSystemEvent(string source)
    {
        bool changed;
        lock (_lock)
        {
            uint dpi = Current;
            changed = dpi != _lastDpi;
            if (changed)
            {
                DiagnosticLogger.Log($"[TaskbarDpi] {source}: changed {_lastDpi} -> {dpi}, redrawing tray icons");
                _lastDpi = dpi;
                _changedInBurst = true;
            }

            // Re-check once things are quiet, even if nothing changed yet: the event can arrive
            // before Explorer has applied the new DPI to the taskbar. Restarting debounces bursts.
            _settleTimer?.Dispose();
            _settleTimer = new Timer(_ => OnSettled(), null, SettleDelayMs, Timeout.Infinite);
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private static void OnSettled()
    {
        lock (_lock)
        {
            uint dpi = Current;
            if (dpi != _lastDpi)
            {
                DiagnosticLogger.Log($"[TaskbarDpi] Changed {_lastDpi} -> {dpi} after events settled, redrawing tray icons");
                _lastDpi = dpi;
            }
            else if (!_changedInBurst)
            {
                return;
            }

            _changedInBurst = false;
        }

        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        RefreshNotifyIconDpiFactors();
        Changed?.Invoke();
    }

    private static readonly MethodInfo? _updateDpiFactors =
        typeof(SystemInfo).GetMethod("UpdateDpiFactors", BindingFlags.Static | BindingFlags.NonPublic);

    /// <summary>
    /// Hardcodet positions the tray context menu by dividing the physical cursor position by
    /// <see cref="SystemInfo.DpiFactorX"/>, which it caches and only refreshes on WM_DPICHANGED.
    /// Its message window is hidden and never receives that message, so after a scale change the
    /// menu opens at the wrong place. Refresh the cached factors ourselves (internal in 2.0.1).
    /// </summary>
    private static void RefreshNotifyIconDpiFactors()
    {
        if (_updateDpiFactors == null)
        {
            DiagnosticLogger.LogWarning("[TaskbarDpi] Hardcodet SystemInfo.UpdateDpiFactors not found; tray menu may be misplaced after a DPI change");
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;

        // UpdateDpiFactors creates an HwndSource, so it must run on the UI thread
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                double before = SystemInfo.DpiFactorX;
                _updateDpiFactors.Invoke(null, null);
                DiagnosticLogger.Log($"[TaskbarDpi] Tray menu DPI factor {before} -> {SystemInfo.DpiFactorX}");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogWarning($"[TaskbarDpi] Failed to refresh tray menu DPI factor: {ex.Message}");
            }
        });
    }
}
