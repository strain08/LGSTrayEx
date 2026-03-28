using LGSTrayPrimitives;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LGSTrayUI.Services;

/// <summary>
/// Encapsulates mode-switch grace period logic for a single device.
/// Delays the "offline" callback by ModeSwitchDetectionDelaySeconds when SuppressModeSwitchNotifications
/// is enabled, so that a wired↔wireless transition is not treated as a real offline event.
/// If the device comes back online before the timer expires, the callback is cancelled and SetOnline
/// returns true (mode switch detected).
/// </summary>
internal sealed class ModeSwitchDetector : IDisposable
{
    private readonly NotificationSettings _settings;
    private CancellationTokenSource? _pendingTimer;
    private readonly object _lock = new();

    public ModeSwitchDetector(NotificationSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Call when the device goes offline.
    /// Invokes <paramref name="onExpired"/> immediately if suppression is disabled or the device
    /// is not in the suppression list; otherwise delays it by ModeSwitchDetectionDelaySeconds.
    /// </summary>
    public void SetOffline(string deviceName, Action onExpired)
    {
        int delaySeconds = GetDelaySeconds(deviceName);

        lock (_lock)
        {
            CancelPendingTimer();

            if (delaySeconds > 0)
            {
                DiagnosticLogger.Log($"[ModeSwitchDetection] {deviceName} went offline - delaying by {delaySeconds}s to detect mode switch");

                var cts = new CancellationTokenSource();
                _pendingTimer = cts;

                Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ContinueWith(task =>
                {
                    if (task.IsCanceled)
                        return;

                    DiagnosticLogger.Log($"[ModeSwitchDetection] {deviceName} still offline after {delaySeconds}s");
                    onExpired();

                    lock (_lock)
                    {
                        if (_pendingTimer == cts)
                        {
                            _pendingTimer.Dispose();
                            _pendingTimer = null;
                        }
                    }
                }, TaskScheduler.Default);

                return;
            }
        }

        // delaySeconds == 0: invoke outside lock to avoid holding it during external callback
        onExpired();
    }

    /// <summary>
    /// Call when the device comes back online.
    /// Cancels any pending offline timer and returns true if a mode switch was detected
    /// (i.e. a timer was pending and was cancelled).
    /// </summary>
    public bool SetOnline(string deviceName)
    {
        lock (_lock)
        {
            if (_pendingTimer == null)
                return false;

            DiagnosticLogger.Log($"[ModeSwitchDetection] {deviceName} came back online - mode switch detected, cancelling pending offline callback");
            CancelPendingTimer();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CancelPendingTimer();
        }
    }

    private int GetDelaySeconds(string deviceName)
    {
        if (!_settings.SuppressModeSwitchNotifications)
            return 0;

        var deviceList = _settings.DevicesForModeSwitchSuppression?.ToList() ?? [];

        // Empty list means apply to all devices
        if (deviceList.Count > 0 && !deviceList.Any(d => deviceName.Contains(d, StringComparison.OrdinalIgnoreCase)))
            return 0;

        return _settings.ModeSwitchDetectionDelaySeconds;
    }

    private void CancelPendingTimer()
    {
        _pendingTimer?.Cancel();
        _pendingTimer?.Dispose();
        _pendingTimer = null;
    }
}
