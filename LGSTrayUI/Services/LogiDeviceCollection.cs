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

public class LogiDeviceCollection : ILogiDeviceCollection
                                    
{
    private readonly UserSettingsWrapper _userSettings;
    private readonly LogiDeviceViewModelFactory _logiDeviceViewModelFactory;
    private readonly ISubscriber<IPCMessage> _subscriber;
    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;

    // Runtime mapping: signature → current deviceId (for GHUB devices with changing IDs)
    private readonly Dictionary<string, string> _signatureToId = new();    

    public ObservableCollection<LogiDeviceViewModel> Devices { get; } = [];
    public IEnumerable<LogiDevice> GetDevices() => Devices;

    public LogiDeviceCollection(
        UserSettingsWrapper userSettings,
        LogiDeviceViewModelFactory logiDeviceViewModelFactory,
        ISubscriber<IPCMessage> subscriber,
        IDispatcher dispatcher,
        IMessenger messenger
    )
    {
        _userSettings = userSettings;
        _logiDeviceViewModelFactory = logiDeviceViewModelFactory;
        _subscriber = subscriber;
        _dispatcher = dispatcher;
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
            existingDevice = Devices.FirstOrDefault(x => x.DeviceId == _signatureToId.GetValueOrDefault(signature));

            // Fallback: search by current deviceId
            existingDevice ??= Devices.FirstOrDefault(x => x.DeviceId == initMessage.deviceId);

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
                if (existingDevice.BatteryPercentage < 0 || !existingDevice.IsOnline)
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

            // Log state transitions
            if (updateMessage.batteryPercentage < 0)
            {
                if (updateMessage.IsWiredMode)
                    DiagnosticLogger.Log($"Device switched to wired mode (charging) - {device.DeviceId} ({device.DeviceName})");
                else
                    DiagnosticLogger.Log($"Device offline, keeping in collection - {device.DeviceId} ({device.DeviceName})");
            }
            else if (device.IsWiredMode)
            {
                DiagnosticLogger.Log($"Device returned to wireless mode - {device.DeviceId} ({device.DeviceName})");
            }

            // Update device state and notify
            device.UpdateState(updateMessage);
            _messenger.Send(new DeviceBatteryUpdatedMessage(device));
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
                    MarkAsOffline(device, removeMessage.reason);
                }

                DiagnosticLogger.Log($"Marked {ghubDevices.Count} GHUB device(s) offline");
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
                    MarkAsOffline(device, removeMessage.reason);
                }

                DiagnosticLogger.Log($"Marked {nativeDevices.Count} Native HID device(s) offline");
                return;
            }

            // Normal single device removal
            var deviceToRemove = Devices.FirstOrDefault(d => d.DeviceId == removeMessage.deviceId);

            if (deviceToRemove == null)
            {
                DiagnosticLogger.LogWarning($"Remove request for unknown device - {removeMessage.deviceId}");
                return;
            }

            // Always mark as offline (KeepOfflineDevices logic handled in UI)
            MarkAsOffline(deviceToRemove, removeMessage.reason);
        });
    }

    /// <summary>
    /// Mark a device as offline but keep it in the collection
    /// </summary>
    private void MarkAsOffline(LogiDeviceViewModel device, string reason)
    {
        DiagnosticLogger.Log($"Marking device offline - {device.DeviceId} ({device.DeviceName}) [Signature: {device.DeviceSignature}] - reason: {reason}");

        device.IsOnline = false;
        device.IsVisuallyOnline = false;  // Set visual state immediately for explicit offline
        device.PowerSupplyStatus = PowerSupplyStatus.UNKNOWN;

        // Notify NotificationService about offline state
        _messenger.Send(new DeviceBatteryUpdatedMessage(device));
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
