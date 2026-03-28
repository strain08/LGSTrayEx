using LGSTrayPrimitives;
using LGSTrayPrimitives.Interfaces;
using LGSTrayUI.Messages;
using MessagePipe;
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

internal class DeviceState
{
    public int? LastNotifiedThreshold { get; set; }
    public bool WasCharging { get; set; } = false;
    public bool WasOnline { get; set; } = false;
    public int LastBatteryPercentage { get; set; }
    public required ModeSwitchDetector ModeSwitchDetector { get; init; }
}
public class NotificationService : IHostedService
{
    private readonly INotificationManager _notificationManager;
    private readonly NotificationSettings _notificationSettings;
    
    private readonly ISubscriber<DeviceBatteryUpdatedMessage> _batterySubscriber;
    private readonly ISubscriber<SystemSuspendingMessage> _suspendSubscriber;
    private readonly ISubscriber<SystemResumingMessage> _resumeSubscriber;
    
    private IDisposable? _subscriptions;

    private readonly Dictionary<string, DeviceState> _deviceStates = [];

    // Track system power state
    private bool _systemSuspended = false;
    private readonly object _powerStateLock = new();

    // Track resume timestamp to suppress spurious offline notifications during device reconnection
    private DateTimeOffset _lastResumeTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan ResumeGracePeriod = TimeSpan.FromSeconds(10);

    public NotificationService(
        INotificationManager notificationManager,
        IOptions<AppSettings> appSettings,
        ISubscriber<DeviceBatteryUpdatedMessage> batterySubscriber,
        ISubscriber<SystemSuspendingMessage> suspendSubscriber,
        ISubscriber<SystemResumingMessage> resumeSubscriber)
    {
        _notificationManager = notificationManager;
        _notificationSettings = appSettings.Value.Notifications;
        
        _batterySubscriber = batterySubscriber;
        _suspendSubscriber = suspendSubscriber;
        _resumeSubscriber = resumeSubscriber;

        // Customize notification colors
        NotificationConstants.InformationBackgroundColor = System.Windows.Media.Brushes.SteelBlue;
        NotificationConstants.WarningBackgroundColor = System.Windows.Media.Brushes.DarkOrange;
        NotificationConstants.SuccessBackgroundColor = System.Windows.Media.Brushes.DarkGreen;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bag = DisposableBag.CreateBuilder();

        // Subscribe to device battery updates from LogiDeviceCollection
        _batterySubscriber.Subscribe(Receive).AddTo(bag);

        // Register for power state messages
        _suspendSubscriber.Subscribe(Receive).AddTo(bag);
        _resumeSubscriber.Subscribe(Receive).AddTo(bag);

        _subscriptions = bag.Build();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriptions?.Dispose();

        foreach (var state in _deviceStates.Values)
            state.ModeSwitchDetector.Dispose();

        return Task.CompletedTask;
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
    /// Handles SystemSuspendingMessage - system is entering suspend/standby.
    /// </summary>
    public void Receive(SystemSuspendingMessage message)
    {
        // System is suspending
        lock (_powerStateLock)
        {
            _systemSuspended = true;
            DiagnosticLogger.Log("NotificationService: Notifications suspended (system entering standby)");
        }
    }

    /// <summary>
    /// Handles SystemResumingMessage - system is resuming from suspend/standby.
    /// </summary>
    public void Receive(SystemResumingMessage message)
    {
        // System has resumed
        lock (_powerStateLock)
        {
            _systemSuspended = false;
            _lastResumeTime = DateTimeOffset.Now;
            DiagnosticLogger.Log($"NotificationService: Notifications resumed (system wake) - " +
                               $"offline notifications suppressed for {ResumeGracePeriod.TotalSeconds}s grace period");
        }
    }
    
    // Predefined thresholds for re-notify battery low
    private int[] GetBatteryLowThresholds() => [_notificationSettings.BatteryLowThreshold, 10, 5];

    public void Receive(DeviceBatteryUpdatedMessage message)
    {
        var device = message.Device;  // Device guaranteed to exist!
        var deviceId = device.DeviceId;
        var deviceName = device.DeviceName;  // Always available, no "Unknown Device"
        var batteryPercent = (int)Math.Round(device.BatteryPercentage);
        var isCharging = device.PowerSupplyStatus == PowerSupplyStatus.CHARGING;
        var isOnline = device.IsOnline;

        DiagnosticLogger.Log($"Received battery update for device {deviceId}: {batteryPercent}%, Charging: {isCharging}");
        
        if (!_notificationSettings.Enabled) return;
        
        // Get or create device state
        if (_deviceStates.TryGetValue(deviceId, out var state))
        {
            // Only show notifications for actual state transitions (not during first-time initialization)
            // Device went offline
            if (state.WasOnline && !isOnline)
            {
                ShowDeviceOfflineNotification(deviceName, deviceId);
            }
            // Device came back online
            else if (!state.WasOnline && isOnline)
            {
                ShowDeviceOnlineNotification(deviceName, deviceId, batteryPercent);
            }
        }
        else
        {
            state = new DeviceState { ModeSwitchDetector = new ModeSwitchDetector(_notificationSettings) };
            _deviceStates[deviceId] = state;
        }

        // Update online state tracking
        state.WasOnline = isOnline;

        // If device is offline, skip battery notification logic
        if (!isOnline)
        {
            state.LastBatteryPercentage = batteryPercent;
            return;
        }

        // Check if we need to show battery almost full notification
        if (isCharging && batteryPercent < 100 && _notificationSettings.NotifyOnBatteryHigh)
        {
            // Show notification if battery reached threshold and wasn't charging before OR crossed threshold
            if (batteryPercent >= _notificationSettings.BatteryHighThreshold && 
                (!state.WasCharging || state.LastBatteryPercentage < _notificationSettings.BatteryHighThreshold))
            {
                ShowBatteryChargedNotification(deviceName, batteryPercent);
            }
        }

        // Show battery fully charged notification if notifications enabled, even if NotifyOnBatteryHigh is disabled
        // _notificationSettings.Enabled check is redundant (service will not be loaded if false) but keeps logic clear
        if (isCharging)
        {
            // Show notification if battery reached threshold and wasn't charging before OR crossed threshold
            if (batteryPercent == 100 && (!state.WasCharging || state.LastBatteryPercentage < 100))
            {
                ShowBatteryChargedNotification(deviceName, batteryPercent);
            }
        }

        // Update charging state tracking and battery percentage
        state.WasCharging = isCharging;
        state.LastBatteryPercentage = batteryPercent;

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
        // Sort thresholds ascending so we find the lowest matching threshold (e.g. 4 <= 5)
        foreach (var threshold in GetBatteryLowThresholds().OrderBy(t => t))
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
            state.LastNotifiedThreshold = null;
            return;
        }

        // Check if we've already notified for this threshold
        if (state.LastNotifiedThreshold.HasValue)
        {
            // Only notify if we've crossed a lower threshold
            if (currentThreshold.Value >= state.LastNotifiedThreshold.Value)
            {
                return;
            }
        }

        // Update the tracking dictionary
        state.LastNotifiedThreshold = currentThreshold.Value;

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

    private void ShowDeviceOfflineNotification(string deviceName, string deviceId)
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

        if (!_deviceStates.TryGetValue(deviceId, out var state))
            return;

        state.ModeSwitchDetector.SetOffline(deviceName, () =>
        {
            var title = $"{deviceName} - Device Offline";
            var message = "Device has been turned off or disconnected";

            _notificationManager.Show(
                title,
                message,
                NotificationType.Warning,
                areaName: "",
                expirationTime: TimeSpan.FromSeconds(5)
            );
        });
    }

    private void ShowDeviceOnlineNotification(string deviceName, string deviceId, int batteryPercent)
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

        // If a pending offline timer was cancelled, this is a mode switch - suppress both notifications
        if (_deviceStates.TryGetValue(deviceId, out var state) && state.ModeSwitchDetector.SetOnline(deviceName))
        {
            DiagnosticLogger.Log($"[ModeSwitchDetection] {deviceName} came back online - mode switch detected, suppressing both notifications");
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
