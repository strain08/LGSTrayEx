using CommunityToolkit.Mvvm.Messaging;
using LGSTrayPrimitives;
using LGSTrayPrimitives.Interfaces;
using LGSTrayUI.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notification.Wpf;
using Notification.Wpf.Constants;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LGSTrayUI.Services;

public class NotificationService : IHostedService,
    IRecipient<SystemSuspendingMessage>,
    IRecipient<SystemResumingMessage>,
    IRecipient<DeviceBatteryUpdatedMessage>
{
    private readonly INotificationManager _notificationManager;
    private readonly NotificationSettings _notificationSettings;
    private readonly IMessenger _messenger;

    // Track the last threshold at which we notified for each device
    private readonly Dictionary<string, int> _lastNotifiedThreshold = [];

    // Track the last charging state for each device
    private readonly Dictionary<string, bool> _lastChargingState = [];

    // Track the last online state for each device
    private readonly Dictionary<string, bool> _lastOnlineState = [];

    // Track pending offline notifications for mode-switch detection
    private readonly Dictionary<string, CancellationTokenSource> _pendingOfflineNotifications = [];
    private readonly object _pendingOfflineLock = new();

    // Track system power state
    private bool _systemSuspended = false;
    private readonly object _powerStateLock = new();

    // Track resume timestamp to suppress spurious offline notifications during device reconnection
    private DateTimeOffset _lastResumeTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan ResumeGracePeriod = TimeSpan.FromSeconds(10);

    public NotificationService(
        INotificationManager notificationManager,
        IOptions<AppSettings> appSettings,
        IMessenger messenger)
    {
        _notificationManager = notificationManager;
        _notificationSettings = appSettings.Value.Notifications;
        _messenger = messenger;
        // Customize notification colors
        NotificationConstants.InformationBackgroundColor = System.Windows.Media.Brushes.SteelBlue;
        NotificationConstants.WarningBackgroundColor = System.Windows.Media.Brushes.DarkOrange;
        NotificationConstants.SuccessBackgroundColor = System.Windows.Media.Brushes.DarkGreen;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe to device battery updates from LogiDeviceCollection
        _messenger.Register<DeviceBatteryUpdatedMessage>(this);

        // Register for power state messages
        _messenger.Register<SystemSuspendingMessage>(this);
        _messenger.Register<SystemResumingMessage>(this);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Unregister from messenger
        _messenger.UnregisterAll(this);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Notify service that system is entering suspend/standby.
    /// Suppresses notifications until Resume() is called.
    /// </summary>
    public void Suspend()
    {
        lock (_powerStateLock)
        {
            _systemSuspended = true;
            DiagnosticLogger.Log("NotificationService: Notifications suspended (system entering standby)");
        }
    }

    /// <summary>
    /// Notify service that system has resumed from suspend/standby.
    /// Resumes normal notification behavior after grace period.
    /// Grace period prevents spurious "device offline" notifications during device reconnection.
    /// </summary>
    public void Resume()
    {
        lock (_powerStateLock)
        {
            _systemSuspended = false;
            _lastResumeTime = DateTimeOffset.Now;
            DiagnosticLogger.Log($"NotificationService: Notifications resumed (system wake) - " +
                               $"offline notifications suppressed for {ResumeGracePeriod.TotalSeconds}s grace period");
        }
    }

    /// <summary>
    /// Check if we're in the grace period after system resume.
    /// Used to suppress spurious offline notifications during device reconnection.
    /// </summary>
    private bool IsInResumeGracePeriod()
    {
        if (_lastResumeTime == DateTimeOffset.MinValue)
            return false;

        var timeSinceResume = DateTimeOffset.Now - _lastResumeTime;
        return timeSinceResume < ResumeGracePeriod;
    }

    /// <summary>
    /// Check if mode switch suppression should be applied to this device.
    /// Returns true if SuppressModeSwitchNotifications is enabled AND either:
    /// - DevicesForModeSwitchSuppression list is empty (apply to all devices), OR
    /// - Device name is in the DevicesForModeSwitchSuppression list
    /// </summary>
    private bool ShouldSuppressModeSwitchForDevice(string deviceName)
    {
        if (!_notificationSettings.SuppressModeSwitchNotifications)
            return false;

        var deviceList = _notificationSettings.DevicesForModeSwitchSuppression?.ToList() ?? [];

        // Empty list means apply to all devices
        if (deviceList.Count == 0)
            return true;

        // Check if device name is in the list (case-insensitive partial match)
        return deviceList.Any(configuredDevice =>
            deviceName.Contains(configuredDevice, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Handles SystemSuspendingMessage - system is entering suspend/standby.
    /// </summary>
    public void Receive(SystemSuspendingMessage message)
    {
        Suspend();
    }

    /// <summary>
    /// Handles SystemResumingMessage - system is resuming from suspend/standby.
    /// </summary>
    public void Receive(SystemResumingMessage message)
    {
        Resume();
    }

    private int[] GetBatteryLowThresholds() =>
        [_notificationSettings.BatteryLowThreshold, 10, 5];

    public void Receive(DeviceBatteryUpdatedMessage message)
    {
        var device = message.Device;  // Device guaranteed to exist!
        var deviceId = device.DeviceId;
        var deviceName = device.DeviceName;  // Always available, no "Unknown Device"
        var batteryPercent = (int)Math.Round(device.BatteryPercentage);
        var isCharging = device.PowerSupplyStatus == PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING;

        DiagnosticLogger.Log($"Received battery update for device {deviceId}: {batteryPercent}%, Charging: {isCharging}");

        // Check device online/offline state transitions
        var isOnline = device.IsOnline;
        var hasSeenDeviceBefore = _lastOnlineState.TryGetValue(deviceId, out var lastOnline);

        // Only show notifications for actual state transitions (not during first-time initialization)
        if (hasSeenDeviceBefore)
        {
            // Device went offline
            if (lastOnline && !isOnline)
            {
                ShowDeviceOfflineNotification(deviceName);
            }
            // Device came back online
            else if (!lastOnline && isOnline)
            {
                ShowDeviceOnlineNotification(deviceName, batteryPercent);
            }
        }

        // Update online state tracking (initialize for new devices, or update for existing)
        _lastOnlineState[deviceId] = isOnline;

        // If device is offline, skip battery notification logic
        if (!isOnline)
        {
            return;
        }

        // Check if we need to show battery almost full notification
        if (isCharging && _notificationSettings.NotifyOnBatteryHigh && batteryPercent < 100)
        {
            // Check if this is a charging state transition (wasn't charging before)
            var wasCharging = _lastChargingState.TryGetValue(deviceId, out var lastCharging) && lastCharging;

            // Show notification if battery reached threshold and wasn't charging before
            if (!wasCharging && batteryPercent >= _notificationSettings.BatteryHighThreshold)
            {
                ShowBatteryChargedNotification(deviceName, batteryPercent);
            }
        }

        // Show battery fully charged notification if notifications enabled, even if NotifyOnBatteryHigh is disabled
        // _notificationSettings.Enabled check is redundant (service will not be loaded if false) but keeps logic clear
        if (isCharging && _notificationSettings.Enabled)
        {
            // Check if this is a charging state transition (wasn't charging before)
            var wasCharging = _lastChargingState.TryGetValue(deviceId, out var lastCharging) && lastCharging;

            // Show notification if battery reached threshold and wasn't charging before
            if (!wasCharging && batteryPercent == 100)
            {
                ShowBatteryChargedNotification(deviceName, batteryPercent);
            }
        }

        // Update charging state tracking
        _lastChargingState[deviceId] = isCharging;

        // If device is charging, skip low battery logic
        if (isCharging)
        {
            return;
        }

        // Low battery notification logic
        if (!_notificationSettings.NotifyOnBatteryLow)
        {
            return;
        }

        // Find the highest threshold that the battery is below
        int? currentThreshold = null;
        foreach (var threshold in GetBatteryLowThresholds())
        {
            if (batteryPercent <= threshold)
            {
                currentThreshold = threshold;
                break;
            }
        }

        // If battery is above all thresholds, reset tracking for this device
        if (currentThreshold == null)
        {
            _lastNotifiedThreshold.Remove(deviceId);
            return;
        }

        // Check if we've already notified for this threshold
        if (_lastNotifiedThreshold.TryGetValue(deviceId, out var lastThreshold))
        {
            // Only notify if we've crossed a lower threshold
            if (currentThreshold.Value >= lastThreshold)
            {
                return;
            }
        }

        // Update the tracking dictionary
        _lastNotifiedThreshold[deviceId] = currentThreshold.Value;

        // Show notification
        ShowLowBatteryNotification(deviceName, batteryPercent, currentThreshold.Value);
    }

    private void ShowLowBatteryNotification(string deviceName, int batteryPercent, int threshold)
    {
        // Check if notifications are suspended
        lock (_powerStateLock)
        {
            if (_systemSuspended)
            {
                DiagnosticLogger.Log($"Notification suppressed (system suspended): {deviceName} - Low Battery {batteryPercent}%");
                return;
            }
        }

        var notificationType = threshold <= 5 ? NotificationType.Error : NotificationType.Warning;
        var title = $"{deviceName} - Battery Low";
        var message = $"Battery level: {batteryPercent}%";

        _notificationManager.Show(
            title,
            message,
            notificationType,
            areaName: "",
            expirationTime: TimeSpan.MaxValue
        );
    }

    private void ShowBatteryChargedNotification(string deviceName, int batteryPercent)
    {
        // Check if notifications are suspended
        lock (_powerStateLock)
        {
            if (_systemSuspended)
            {
                DiagnosticLogger.Log($"Notification suppressed (system suspended): {deviceName} - Battery Charged {batteryPercent}%");
                return;
            }
        }
        // distinct message for fully charged vs almost full (high threshold)
        var title = batteryPercent == 100 ? $"{deviceName} - Battery Full" : $"{deviceName} - Battery almost full (charging)";
        var message = $"Battery level: {batteryPercent}%";

        _notificationManager.Show(
            title,
            message,
            NotificationType.Success,
            areaName: "",
            expirationTime: TimeSpan.FromSeconds(5)
        );
    }

    private void ShowDeviceOfflineNotification(string deviceName)
    {
        // Check if notifications are suspended or in resume grace period
        lock (_powerStateLock)
        {
            if (_systemSuspended)
            {
                DiagnosticLogger.Log($"Notification suppressed (system suspended): {deviceName} - Device Offline");
                return;
            }

            if (IsInResumeGracePeriod())
            {
                var timeSinceResume = (DateTimeOffset.Now - _lastResumeTime).TotalSeconds;
                DiagnosticLogger.Log($"Notification suppressed (resume grace period +{timeSinceResume:F1}s): {deviceName} - Device Offline");
                return;
            }
        }

        if (!_notificationSettings.NotifyStateChange)
        {
            DiagnosticLogger.Log($"Skipping offline notification for {deviceName} as NotifyStateChange is disabled in settings.");
            return;
        }

        // Check if mode switch suppression is enabled for this device
        if (ShouldSuppressModeSwitchForDevice(deviceName))
        {
            var delaySeconds = _notificationSettings.ModeSwitchDetectionDelaySeconds;
            DiagnosticLogger.Log($"[ModeSwitchDetection] {deviceName} went offline - delaying notification by {delaySeconds}s to detect mode switch");

            lock (_pendingOfflineLock)
            {
                // Cancel any existing pending notification for this device
                if (_pendingOfflineNotifications.TryGetValue(deviceName, out var existingCts))
                {
                    existingCts.Cancel();
                    existingCts.Dispose();
                }

                // Create new cancellation token for this pending notification
                var cts = new CancellationTokenSource();
                _pendingOfflineNotifications[deviceName] = cts;

                // Schedule delayed notification
                Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ContinueWith(task =>
                {
                    if (task.IsCanceled)
                    {
                        // Notification was cancelled (device came back online - mode switch detected)
                        DiagnosticLogger.Log($"[ModeSwitchDetection] {deviceName} offline notification cancelled (mode switch detected)");
                        return;
                    }

                    // Delay expired, device still offline - show notification on UI thread
                    DiagnosticLogger.Log($"[ModeSwitchDetection] {deviceName} still offline after {delaySeconds}s - showing notification");

                    var title = $"{deviceName} - Device Offline";
                    var message = "Device has been turned off or disconnected";

                    _notificationManager.Show(
                        title,
                        message,
                        NotificationType.Warning,
                        areaName: "",
                        expirationTime: TimeSpan.FromSeconds(5)
                    );

                    // Clean up
                    lock (_pendingOfflineLock)
                    {
                        _pendingOfflineNotifications.Remove(deviceName);
                        cts.Dispose();
                    }
                }, TaskScheduler.Default);
            }

            return;
        }

        // Mode switch suppression not enabled - show notification immediately
        var notificationTitle = $"{deviceName} - Device Offline";
        var notificationMessage = "Device has been turned off or disconnected";

        _notificationManager.Show(
            notificationTitle,
            notificationMessage,
            NotificationType.Warning,
            areaName: "",
            expirationTime: TimeSpan.FromSeconds(5)
        );
    }

    private void ShowDeviceOnlineNotification(string deviceName, int batteryPercent)
    {
        // Check if notifications are suspended
        lock (_powerStateLock)
        {
            if (_systemSuspended)
            {
                DiagnosticLogger.Log($"Notification suppressed (system suspended): {deviceName} - Device Online");
                return;
            }
        }

        if (!_notificationSettings.NotifyStateChange)
        {
            DiagnosticLogger.Log($"Skipping online notification for {deviceName} as NotifyStateChange is disabled in settings.");
            return;
        }

        // Check if there's a pending offline notification (mode switch detection)
        bool modeSwitchDetected = false;
        lock (_pendingOfflineLock)
        {
            if (_pendingOfflineNotifications.TryGetValue(deviceName, out var cts))
            {
                // Mode switch detected - cancel the pending offline notification
                cts.Cancel();
                cts.Dispose();
                _pendingOfflineNotifications.Remove(deviceName);
                modeSwitchDetected = true;

                DiagnosticLogger.Log($"[ModeSwitchDetection] {deviceName} came back online - mode switch detected, suppressing both notifications");
            }
        }

        // If mode switch was detected, suppress the online notification as well
        if (modeSwitchDetected)
        {
            return;
        }

        // Normal online notification (not a mode switch)
        var title = $"{deviceName} - Device Online";
        var message = $"Device is back online (Battery: {batteryPercent}%)";

        _notificationManager.Show(
            title,
            message,
            NotificationType.Information,
            areaName: "",
            expirationTime: TimeSpan.FromSeconds(5)
        );
    }
}
