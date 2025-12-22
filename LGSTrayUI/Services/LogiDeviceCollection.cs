using LGSTrayCore;
using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayPrimitives.Interfaces;
using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace LGSTrayUI;

public class LogiDeviceCollection : ILogiDeviceCollection
{
    private readonly UserSettingsWrapper _userSettings;
    private readonly LogiDeviceViewModelFactory _logiDeviceViewModelFactory;
    private readonly ISubscriber<IPCMessage> _subscriber;
    private readonly IDispatcher _dispatcher;
    private const int STUB_CLEANUP_DELAY_MS = 30_000; // 30 seconds

    public ObservableCollection<LogiDeviceViewModel> Devices { get; } = [];
    public IEnumerable<LogiDevice> GetDevices() => Devices;

    public LogiDeviceCollection(
        UserSettingsWrapper userSettings,
        LogiDeviceViewModelFactory logiDeviceViewModelFactory,
        ISubscriber<IPCMessage> subscriber,
        IDispatcher dispatcher
    )
    {
        _userSettings = userSettings;
        _logiDeviceViewModelFactory = logiDeviceViewModelFactory;
        _subscriber = subscriber;
        _dispatcher = dispatcher;

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

        LoadPreviouslySelectedDevices();
    }

    private void LoadPreviouslySelectedDevices()
    {
        // Deduplicate settings first
        DeduplicateSettings();

        foreach (var deviceId in _userSettings.SelectedDevices)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                continue;
            }

            Devices.Add(
                _logiDeviceViewModelFactory.CreateViewModel((x) =>
                {
                    x.DeviceId = deviceId!;
                    x.DeviceName = "Not Initialised";
                    x.IsChecked = true;
                })
            );
        }

        // Schedule cleanup of stale stubs after grace period
        if (Devices.Any())
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(STUB_CLEANUP_DELAY_MS);
                CleanupStaleStubs();
            });
        }
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
            LogiDeviceViewModel? dev = Devices.SingleOrDefault(x => x.DeviceId == initMessage.deviceId);

            // Device already exists - just update it
            if (dev != null)
            {
                dev.UpdateState(initMessage);
                return;
            }

            // Check if this is a GHUB device reconnecting with new ID
            // Look for uninitialized stub to replace
            if (IsLikelyGHubIdChange(initMessage.deviceId))
            {
                var stub = Devices.FirstOrDefault(d => IsUninitializedStub(d));

                if (stub != null)
                {
                    DiagnosticLogger.Log($"Replacing stub {stub.DeviceId} with {initMessage.deviceId} ({initMessage.deviceName})");

                    // Transfer selection state
                    bool wasSelected = stub.IsChecked;

                    // Uncheck stub to dispose icon before removing from collection
                    if (stub.IsChecked)
                    {
                        stub.IsChecked = false;
                    }

                    // Remove old stub
                    _userSettings.RemoveDevice(stub.DeviceId);
                    Devices.Remove(stub);

                    // Add new device with transferred selection
                    var newDevice = _logiDeviceViewModelFactory.CreateViewModel((x) =>
                    {
                        x.UpdateState(initMessage);
                        x.IsChecked = wasSelected;
                    });

                    Devices.Add(newDevice);
                    _userSettings.AddDevice(initMessage.deviceId);

                    DiagnosticLogger.Log($"Stub replacement complete - {initMessage.deviceId}");
                    return;
                }
            }

            // Normal new device addition
            dev = _logiDeviceViewModelFactory.CreateViewModel((x) => x.UpdateState(initMessage));
            Devices.Add(dev);
            DiagnosticLogger.Log($"Device added to collection - {initMessage.deviceId} ({initMessage.deviceName})");
        });
    }

    public void OnUpdateMessage(UpdateMessage updateMessage)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var device = Devices.FirstOrDefault(dev => dev.DeviceId == updateMessage.deviceId);
            if (device == null)
            {
                DiagnosticLogger.LogWarning($"Update for unknown device - {updateMessage.deviceId}");
                return;
            }

            device.UpdateState(updateMessage);
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

                // Remove all devices with IDs starting with "dev" (GHUB convention)
                var ghubDevices = Devices.Where(d => d.DeviceId.StartsWith("dev")).ToList();

                foreach (var device in ghubDevices)
                {
                    RemoveDevice(device, removeMessage.reason);
                }

                DiagnosticLogger.Log($"Removed {ghubDevices.Count} GHUB device(s)");
                return;
            }

            // Normal single device removal
            var deviceToRemove = Devices.FirstOrDefault(d => d.DeviceId == removeMessage.deviceId);

            if (deviceToRemove == null)
            {
                DiagnosticLogger.LogWarning($"Remove request for unknown device - {removeMessage.deviceId}");
                return;
            }

            RemoveDevice(deviceToRemove, removeMessage.reason);
        });
    }

    /// <summary>
    /// Remove a device from the collection and clean up resources
    /// </summary>
    private void RemoveDevice(LogiDeviceViewModel device, string reason)
    {
        DiagnosticLogger.Log($"Removing device - {device.DeviceId} ({device.DeviceName}) - reason: {reason}");

        // Uncheck to release icon resources
        if (device.IsChecked)
        {
            device.IsChecked = false;
        }

        // Remove from settings
        _userSettings.RemoveDevice(device.DeviceId);

        // Remove from collection (triggers UI update via ObservableCollection)
        Devices.Remove(device);
    }

    /// <summary>
    /// Check if a device is a "Not Initialised" stub from settings
    /// </summary>
    private bool IsUninitializedStub(LogiDeviceViewModel device)
    {
        return device.DeviceName == "Not Initialised";
    }

    /// <summary>
    /// Check if ID change is likely from GHUB sleep/wake
    /// GHUB devices have "dev" prefix and numeric IDs
    /// </summary>
    private bool IsLikelyGHubIdChange(string deviceId)
    {
        return deviceId.StartsWith("dev") && deviceId.Length > 3;
    }

    /// <summary>
    /// Remove duplicate and empty device IDs from settings
    /// </summary>
    private void DeduplicateSettings()
    {
        var deviceIds = _userSettings.SelectedDevices.Cast<string>()
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        if (deviceIds.Count != _userSettings.SelectedDevices.Count)
        {
            DiagnosticLogger.Log($"Deduplicating settings: {_userSettings.SelectedDevices.Count} → {deviceIds.Count}");

            _userSettings.SelectedDevices.Clear();
            foreach (var id in deviceIds)
            {
                _userSettings.SelectedDevices.Add(id);
            }
            Properties.Settings.Default.Save();
        }
    }

    /// <summary>
    /// Remove devices still showing "Not Initialised" after timeout
    /// These are likely from old IDs that will never initialize
    /// </summary>
    private void CleanupStaleStubs()
    {
        _dispatcher.BeginInvoke(() =>
        {
            var staleStubs = Devices.Where(d => IsUninitializedStub(d)).ToList();

            if (staleStubs.Any())
            {
                DiagnosticLogger.Log($"Cleaning up {staleStubs.Count} stale stub(s)");

                foreach (var stub in staleStubs)
                {
                    DiagnosticLogger.Log($"Removing stale stub - {stub.DeviceId}");

                    // Uncheck stub to dispose icon before removing from collection
                    if (stub.IsChecked)
                    {
                        stub.IsChecked = false;
                    }

                    _userSettings.RemoveDevice(stub.DeviceId);
                    Devices.Remove(stub);
                }
            }
        });
    }
}
