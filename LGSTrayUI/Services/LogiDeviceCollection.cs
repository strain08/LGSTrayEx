using CommunityToolkit.Mvvm.Messaging;
using LGSTrayCore;
using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayPrimitives.Interfaces;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayUI.Messages;
using LGSTrayUI.Services;
using MessagePipe;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace LGSTrayUI;

public class LogiDeviceCollection : ILogiDeviceCollection,
    IRecipient<SystemResumingMessage>
{
    private readonly UserSettingsWrapper _userSettings;
    private readonly LogiDeviceViewModelFactory _logiDeviceViewModelFactory;
    private readonly ISubscriber<IPCMessage> _subscriber;
    private readonly IDispatcher _dispatcher;
    private readonly AppSettings _appSettings;
    private readonly IMessenger _messenger;

    // Runtime mapping: signature → current deviceId (for GHUB devices with changing IDs)
    private readonly Dictionary<string, string> _signatureToId = new();

    // Grace period tracking to ignore battery updates after system resume
    private DateTimeOffset _lastResumeTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan ResumeGracePeriod = TimeSpan.FromSeconds(10);
    private readonly object _resumeLock = new();

    public ObservableCollection<LogiDeviceViewModel> Devices { get; } = [];
    public IEnumerable<LogiDevice> GetDevices() => Devices;

    public LogiDeviceCollection(
        UserSettingsWrapper userSettings,
        LogiDeviceViewModelFactory logiDeviceViewModelFactory,
        ISubscriber<IPCMessage> subscriber,
        IDispatcher dispatcher,
        AppSettings appSettings,
        IMessenger messenger
    )
    {
        _userSettings = userSettings;
        _logiDeviceViewModelFactory = logiDeviceViewModelFactory;
        _subscriber = subscriber;
        _dispatcher = dispatcher;
        _appSettings = appSettings;
        _messenger = messenger;

        _subscriber.Subscribe(x =>
        {
            if (x is InitMessage initMessage)
            {
                OnInitMessage(initMessage);
            }
            else if (x is UpdateMessage updateMessage)
            {
                OnUpdateMessage(updateMessage);
            }
            else if (x is RemoveMessage removeMessage)
            {
                OnRemoveMessage(removeMessage);
            }
        });

        // Register for system resume messages to enable grace period
        _messenger.Register<SystemResumingMessage>(this);

        LoadPreviouslySelectedDevices();
    }

    private void LoadPreviouslySelectedDevices()
    {
        // MIGRATION: Clear old deviceId-based settings (manual re-selection approach)
        if (_userSettings.SelectedDevices.Count > 0)
        {
            DiagnosticLogger.Log($"MIGRATION: Clearing {_userSettings.SelectedDevices.Count} old device ID settings");
            DiagnosticLogger.Log("Users will need to re-select their devices (signature-based matching)");
            _userSettings.SelectedDevices.Clear();
            Properties.Settings.Default.Save();
        }

        // Deduplicate settings first
        DeduplicateSettings();

        // Note: We no longer load stubs for previously selected devices.
        // Signature-based matching will restore selection when devices reconnect.
        // This eliminates the need for stub cleanup logic.

        DiagnosticLogger.Log($"Loaded {_userSettings.SelectedSignatures.Count} device signature(s) from settings");
    }

    public bool TryGetDevice(string deviceId, [NotNullWhen(true)] out LogiDevice? device)
    {
        device = Devices.SingleOrDefault(x => x.DeviceId == deviceId);

        return device != null;
    }

    /// <summary>
    /// Handles SystemResumingMessage - system is resuming from suspend/standby.
    /// Enables grace period to ignore incorrect battery updates.
    /// </summary>
    public void Receive(SystemResumingMessage message)
    {
        lock (_resumeLock)
        {
            _lastResumeTime = DateTimeOffset.Now;
            DiagnosticLogger.Log($"LogiDeviceCollection: Resume detected - " +
                $"battery updates suppressed for {ResumeGracePeriod.TotalSeconds}s");
        }
    }

    /// <summary>
    /// Check if we're in the grace period after system resume.
    /// Used to ignore incorrect battery updates from devices reconnecting.
    /// </summary>
    private bool IsInResumeGracePeriod()
    {
        lock (_resumeLock)
        {
            if (_lastResumeTime == DateTimeOffset.MinValue)
                return false;

            var timeSinceResume = DateTimeOffset.Now - _lastResumeTime;
            return timeSinceResume < ResumeGracePeriod;
        }
    }

    public void OnInitMessage(InitMessage initMessage)
    {
        // Marshal ALL Devices collection access to UI thread to prevent
        // InvalidOperationException during enumeration
        _dispatcher.BeginInvoke(() =>
        {
            // Get signature from message (should always be present now)
            string? signature = initMessage.deviceSignature;

            if (string.IsNullOrEmpty(signature))
            {
                DiagnosticLogger.LogWarning($"Device {initMessage.deviceId} has no signature - using deviceId as fallback");
                signature = initMessage.deviceId;
            }

            // Check if device already exists by signature (not deviceId, as GHUB changes IDs)
            LogiDeviceViewModel? existingDevice = null;

            // First, try to find by signature in our mapping
            if (_signatureToId.TryGetValue(signature, out string? mappedDeviceId))
            {
                existingDevice = Devices.FirstOrDefault(x => x.DeviceId == mappedDeviceId);
            }

            // Fallback: search by current deviceId
            if (existingDevice == null)
            {
                existingDevice = Devices.FirstOrDefault(x => x.DeviceId == initMessage.deviceId);
            }

            // Device already exists - update it
            if (existingDevice != null)
            {
                // Check if deviceId changed (GHUB ID change scenario)
                if (existingDevice.DeviceId != initMessage.deviceId)
                {
                    DiagnosticLogger.Log($"Device ID changed: {existingDevice.DeviceId} → {initMessage.deviceId} (Signature: {signature})");
                    existingDevice.DeviceId = initMessage.deviceId;
                }

                DiagnosticLogger.Log($"Device already exists, updating - {initMessage.deviceId} ({initMessage.deviceName})");

                // Log if device was offline and is now reconnecting
                if (existingDevice.BatteryPercentage < 0)
                {
                    DiagnosticLogger.Log($"Device reconnected from offline state - {initMessage.deviceId}");
                }

                existingDevice.UpdateState(initMessage);

                // Update signature mapping
                _signatureToId[signature] = initMessage.deviceId;

                // Restore IsChecked from signature-based settings
                if (!existingDevice.IsChecked && _userSettings.ContainsSignature(signature))
                {
                    existingDevice.IsChecked = true;
                    DiagnosticLogger.Log($"Restored selection state for existing device - {signature}");
                }

                return;
            }

            // NEW DEVICE - Create and add it
            var newDevice = _logiDeviceViewModelFactory.CreateViewModel((x) => x.UpdateState(initMessage));

            // Update signature → deviceId mapping
            _signatureToId[signature] = initMessage.deviceId;

            // Restore selection based on signature
            if (_userSettings.ContainsSignature(signature))
            {
                newDevice.IsChecked = true;
                DiagnosticLogger.Log($"Restored selection for new device - {signature}");
            }

            Devices.Add(newDevice);
            DiagnosticLogger.Log($"Device added to collection - {initMessage.deviceId} ({initMessage.deviceName}) [Signature: {signature}]");
        });
    }

    public void OnUpdateMessage(UpdateMessage updateMessage)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // Note: Grace period check removed - battery event filtering now handled
            // in HidppDevice.TryHandleBatteryEventAsync() with device-specific delay window

            var device = Devices.FirstOrDefault(dev => dev.DeviceId == updateMessage.deviceId);
            if (device == null)
            {
                DiagnosticLogger.LogWarning($"Update for unknown device - {updateMessage.deviceId}");
                return;
            }

            // Check if device is going offline (batteryPercentage = -1)
            if (updateMessage.batteryPercentage < 0)
            {
                // Check if this is a mode switch (wired mode)
                if (updateMessage.IsWiredMode)
                {
                    // Mode switch detected - keep device in collection with wired mode status
                    DiagnosticLogger.Log($"Device switched to wired mode (charging) - {device.DeviceId} ({device.DeviceName})");
                    device.UpdateState(updateMessage);

                    // Notify NotificationService about mode switch
                    _messenger.Send(new DeviceBatteryUpdatedMessage(device));
                }
                else if (_appSettings.UI.KeepOfflineDevices)
                {
                    // Keep device in collection, update with offline state
                    DiagnosticLogger.Log($"Device offline, keeping in collection - {device.DeviceId} ({device.DeviceName})");
                    device.UpdateState(updateMessage);

                    // Notify NotificationService that device battery was updated (offline state)
                    // Device is guaranteed to be in collection and fully updated
                    _messenger.Send(new DeviceBatteryUpdatedMessage(device));
                }
                else
                {
                    // Device going offline - update state and notify BEFORE removing
                    DiagnosticLogger.Log($"Device offline, removing from collection - {device.DeviceId} ({device.DeviceName})");
                    device.UpdateState(updateMessage);

                    // Notify NotificationService about offline state before removal
                    // This ensures offline notification is shown
                    _messenger.Send(new DeviceBatteryUpdatedMessage(device));

                    // Now remove device from collection
                    RemoveDevice(device, "device_offline");
                }
            }
            else
            {
                // Normal battery update (not offline)
                // If device was in wired mode and now has battery data, it returned to wireless
                if (device.IsWiredMode)
                {
                    DiagnosticLogger.Log($"Device returned to wireless mode - {device.DeviceId} ({device.DeviceName})");
                }

                device.UpdateState(updateMessage);

                // Notify NotificationService that device battery was updated
                // Device is guaranteed to be in collection and fully updated
                _messenger.Send(new DeviceBatteryUpdatedMessage(device));
            }
        });
    }

    /// <summary>
    /// Handle device removal requests from IPC
    /// Supports wildcard patterns like "*GHUB*" to remove multiple devices
    /// </summary>
    public void OnRemoveMessage(RemoveMessage removeMessage)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // Handle wildcard GHUB removal pattern
            if (removeMessage.deviceId == "*GHUB*")
            {
                DiagnosticLogger.Log($"Wildcard GHUB removal requested (reason: {removeMessage.reason})");

                // Remove all devices with GHub data source
                var ghubDevices = Devices.Where(d => d.DataSource == DataSource.GHub).ToList();

                foreach (var device in ghubDevices)
                {
                    RemoveDevice(device, removeMessage.reason);
                }

                DiagnosticLogger.Log($"Removed {ghubDevices.Count} GHUB device(s)");
                return;
            }

            // Handle wildcard Native HID removal pattern
            if (removeMessage.deviceId == "*NATIVE*")
            {
                DiagnosticLogger.Log($"Wildcard Native HID removal requested (reason: {removeMessage.reason})");

                // Remove all devices with Native data source
                var nativeDevices = Devices.Where(d => d.DataSource == DataSource.Native).ToList();

                foreach (var device in nativeDevices)
                {
                    RemoveDevice(device, removeMessage.reason);
                }

                DiagnosticLogger.Log($"Removed {nativeDevices.Count} Native HID device(s)");
                return;
            }

            // Normal single device removal
            var deviceToRemove = Devices.FirstOrDefault(d => d.DeviceId == removeMessage.deviceId);

            if (deviceToRemove == null)
            {
                DiagnosticLogger.LogWarning($"Remove request for unknown device - {removeMessage.deviceId}");
                return;
            }

            // Check keepOfflineDevices setting
            if (_appSettings.UI.KeepOfflineDevices)
            {
                // Mark device as offline instead of removing
                DiagnosticLogger.Log($"Marking device offline (keepOfflineDevices=true) - {deviceToRemove.DeviceId} ({deviceToRemove.DeviceName}) - reason: {removeMessage.reason}");
                deviceToRemove.BatteryPercentage = -1;
                deviceToRemove.PowerSupplyStatus = PowerSupplyStatus.POWER_SUPPLY_STATUS_UNKNOWN;
                // Icon will automatically update to show "?" or "Missing" icon
            }
            else
            {
                // Original behavior: Remove device entirely
                RemoveDevice(deviceToRemove, removeMessage.reason);
            }
        });
    }

    /// <summary>
    /// Remove a device from the collection and clean up resources
    /// </summary>
    private void RemoveDevice(LogiDeviceViewModel device, string reason)
    {
        // Check keepOfflineDevices setting for wildcard removals
        if (_appSettings.UI.KeepOfflineDevices)
        {
            DiagnosticLogger.Log($"Marking device offline (wildcard, keepOfflineDevices=true) - {device.DeviceId}");
            device.BatteryPercentage = -1;
            device.PowerSupplyStatus = PowerSupplyStatus.POWER_SUPPLY_STATUS_UNKNOWN;
            return; // Don't actually remove
        }

        DiagnosticLogger.Log($"Removing device - {device.DeviceId} ({device.DeviceName}) [Signature: {device.DeviceSignature}] - reason: {reason}");

        // Uncheck to release icon resources
        if (device.IsChecked)
        {
            device.IsChecked = false;
        }

        // Dispose the ViewModel to clean up event subscriptions (prevents memory leak)
        device.Dispose();

        // Note: With signature-based matching, we preserve settings for ALL disconnect reasons
        // Settings are only removed when user manually unchecks the device
        // This allows devices to seamlessly reconnect with new IDs (GHUB) or after being offline

        DiagnosticLogger.Log($"Device settings preserved for reconnection - {device.DeviceSignature}");

        // Remove from collection (triggers UI update via ObservableCollection)
        Devices.Remove(device);

        // Remove from signature mapping
        if (_signatureToId.ContainsKey(device.DeviceSignature))
        {
            _signatureToId.Remove(device.DeviceSignature);
        }
    }

    /// <summary>
    /// Remove duplicate and empty device signatures from settings
    /// </summary>
    private void DeduplicateSettings()
    {
        var signatures = _userSettings.SelectedSignatures.Cast<string>()
            .Where(sig => !string.IsNullOrWhiteSpace(sig))
            .Distinct()
            .ToList();

        if (signatures.Count != _userSettings.SelectedSignatures.Count)
        {
            DiagnosticLogger.Log($"Deduplicating signature settings: {_userSettings.SelectedSignatures.Count} → {signatures.Count}");

            _userSettings.SelectedSignatures.Clear();
            foreach (var sig in signatures)
            {
                _userSettings.SelectedSignatures.Add(sig);
            }
            Properties.Settings.Default.Save();
        }
    }
}
