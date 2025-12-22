using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notification.Wpf;
using Notification.Wpf.Constants;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LGSTrayUI;

public class NotificationService : IHostedService
{
    private readonly INotificationManager _notificationManager;
    private readonly ISubscriber<IPCMessage> _subscriber;
    private readonly ILogiDeviceCollection _deviceCollection;
    private readonly NotificationSettings _notificationSettings;
    private IDisposable? _subscription;

    // Track the last threshold at which we notified for each device
    private readonly Dictionary<string, int> _lastNotifiedThreshold = new();

    // Track the last charging state for each device
    private readonly Dictionary<string, bool> _lastChargingState = new();

    // Track the last online state for each device
    private readonly Dictionary<string, bool> _lastOnlineState = new();

    public NotificationService(
        INotificationManager notificationManager,
        ISubscriber<IPCMessage> subscriber,
        ILogiDeviceCollection deviceCollection,
        IOptions<AppSettings> appSettings)
    {
        _notificationManager = notificationManager;
        _subscriber = subscriber;
        _deviceCollection = deviceCollection;
        _notificationSettings = appSettings.Value.Notifications;
        // Customize notification colors
        NotificationConstants.InformationBackgroundColor = System.Windows.Media.Brushes.SteelBlue;
        NotificationConstants.WarningBackgroundColor = System.Windows.Media.Brushes.DarkOrange;
        NotificationConstants.SuccessBackgroundColor = System.Windows.Media.Brushes.DarkGreen;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe to device update messages
        _subscription = _subscriber.Subscribe(message =>
        {
            if (message is UpdateMessage updateMessage)
            {
                OnBatteryUpdate(updateMessage);
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Clean up subscription
        _subscription?.Dispose();

        return Task.CompletedTask;
    }

    private int[] GetBatteryLowThresholds() =>
        [_notificationSettings.BatteryLowThreshold, 10, 5];

    private void OnBatteryUpdate(UpdateMessage updateMessage)
    {
        var batteryPercent = (int)Math.Round(updateMessage.batteryPercentage);
        var isCharging = updateMessage.powerSupplyStatus == PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING;
        var deviceId = updateMessage.deviceId;
        DiagnosticLogger.Log($"Received battery update for device {deviceId}: {batteryPercent}%, Charging: {isCharging}");
        // Get device name once
        string deviceName = "Unknown Device";
        var device = _deviceCollection.GetDevices().FirstOrDefault(d => d.DeviceId == deviceId);
        if (device != null)
        {
            deviceName = device.DeviceName;
        }

        // Check device online/offline state transitions
        var isOnline = batteryPercent >= 0;
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

        // Check if we need to show battery charged notification
        if (isCharging && _notificationSettings.NotifyOnBatteryHigh)
        {
            // Check if this is a charging state transition (wasn't charging before)
            var wasCharging = _lastChargingState.TryGetValue(deviceId, out var lastCharging) && lastCharging;

            // Show notification if battery reached threshold and wasn't charging before
            if (!wasCharging && batteryPercent >= _notificationSettings.BatteryHighThreshold)
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
        var notificationType = threshold <= 5 ? NotificationType.Error : NotificationType.Warning;
        var title = $"{deviceName} - Low Battery";
        var message = $"Battery level: {batteryPercent}%";

        _notificationManager.Show(
            title,
            message,
            notificationType,
            areaName: "",
            expirationTime: TimeSpan.FromSeconds(5)
        );
    }

    private void ShowBatteryChargedNotification(string deviceName, int batteryPercent)
    {
        var title = $"{deviceName} - Battery Charged";
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
        var title = $"{deviceName} - Device Offline";
        var message = "Device has been turned off or disconnected";

        _notificationManager.Show(
            title,
            message,
            NotificationType.Warning,
            areaName: "",
            expirationTime: TimeSpan.FromSeconds(5)
        );
    }

    private void ShowDeviceOnlineNotification(string deviceName, int batteryPercent)
    {
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
