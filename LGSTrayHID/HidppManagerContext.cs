using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using System.Collections.Concurrent;
using System.Linq;
using static LGSTrayHID.HidApi.HidApi;
using static LGSTrayHID.HidApi.HidApiHotPlug;
using static LGSTrayHID.HidApi.HidApiWinApi;

namespace LGSTrayHID;

public sealed class HidppManagerContext
{
    public static readonly HidppManagerContext _instance = new();
    public static HidppManagerContext Instance => _instance;

    private readonly Dictionary<string, Guid> _containerMap = [];
    private readonly Dictionary<Guid, HidppReceiver> _deviceMap = [];
    private readonly BlockingCollection<HidDeviceInfo> _deviceQueue = [];

    // Mode-switch detection: Track recent USB device arrivals
    private readonly object _arrivalLock = new();
    private readonly List<UsbArrivalRecord> _recentArrivals = [];
    private static readonly TimeSpan ArrivalTrackingWindow = TimeSpan.FromSeconds(5);

    private record UsbArrivalRecord(DateTimeOffset Timestamp, ushort ProductId, Guid ContainerId, string DevicePath);

    public delegate void HidppDeviceEventHandler(IPCMessageType messageType, IPCMessage message);

    public event HidppDeviceEventHandler? HidppDeviceEvent;

    private HidppManagerContext() { }

    static HidppManagerContext()
    {
        _ = HidInit();
    }

    public void Start(CancellationToken cancellationToken)
    {
        new Thread(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var dev = _deviceQueue.Take(); // blocking call
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await InitDevice(dev);
            }
        }).Start();

        unsafe
        {
            HidHotplugRegisterCallback(0x046D,
                                       0x00,
                                       HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_ARRIVED,
                                       HidApiHotPlugFlag.HID_API_HOTPLUG_ENUMERATE,
                                       DeviceArrived,
                                       IntPtr.Zero,
                                       (int*)IntPtr.Zero);
            HidHotplugRegisterCallback(0x046D,
                                       0x00,
                                       HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_LEFT,
                                       HidApiHotPlugFlag.NONE,
                                       DeviceLeft,
                                       IntPtr.Zero,
                                       (int*)IntPtr.Zero);
        }
    }


    private async Task<int> InitDevice(HidDeviceInfo deviceInfo)
    {
        var messageType = deviceInfo.GetHidppMessageType();
        switch (messageType)
        {
            case HidppMessageType.NONE:
            case HidppMessageType.VERY_LONG:
                DiagnosticLogger.Log($"Skipping device with unsupported message type: {messageType}");
                return 0;
        }

        string devPath = deviceInfo.GetPath();
        DiagnosticLogger.Log($"Initializing HID device: {devPath}");

        HidDevicePtr dev = HidOpenPath(ref deviceInfo);
        _ = HidWinApiGetContainerId(dev, out Guid containerId);

        DiagnosticLogger.Log(devPath);
        DiagnosticLogger.Log(containerId.ToString());
        DiagnosticLogger.Log($"Usage: {deviceInfo.Usage:X04}");
        DiagnosticLogger.Log($"Page : {deviceInfo.UsagePage:X04}");
        DiagnosticLogger.Log("");

        // Track USB arrival for mode-switch detection
        TrackUsbArrival(deviceInfo.ProductId, containerId, devPath);

        // Check if this is likely a wired mode device (recent mode switch detected)
        bool isWiredModeDevice = CheckForRecentUsbArrival(out ushort wiredPid);
        if (isWiredModeDevice)
        {
            DiagnosticLogger.Log($"[Mode-Switch] Wired device detected - PID: 0x{wiredPid:X04}, " +
                                $"will use fast-track initialization");
        }

        if (!_deviceMap.TryGetValue(containerId, out HidppReceiver? hidppReceiver))
        {
            hidppReceiver = new(GlobalSettings.settings.KeepPollingWithEvents, GlobalSettings.settings.BatteryEventDelayAfterOn);
            _deviceMap[containerId] = hidppReceiver;
            _containerMap[devPath] = containerId;
            DiagnosticLogger.Log($"New container created - Path: {devPath}, Container: {containerId}");
        }
        else
        {
            DiagnosticLogger.Log($"Existing container found - Path: {devPath}, Container: {containerId}");
        }

        await hidppReceiver.SetUp(messageType, dev, isWiredModeDevice);

        return 0;
    }

    private unsafe int DeviceArrived(HidHotPlugCallbackHandle _, HidDeviceInfo* device, HidApiHotPlugEvent hidApiHotPlugEvent, nint __)
    {
        if (hidApiHotPlugEvent == HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_ARRIVED)
        {
            string devPath = (*device).GetPath();
            DiagnosticLogger.Log($"HID device arrival detected: {devPath}");
            _deviceQueue.Add(*device);
        }

        return 0;
    }

    private unsafe int DeviceLeft(HidHotPlugCallbackHandle callbackHandle, HidDeviceInfo* deviceInfo, HidApiHotPlugEvent hidApiHotPlugEvent, nint userData)
    {
        string devPath = (*deviceInfo).GetPath();

        // TEST HARNESS: Log device removal event with path
        DiagnosticLogger.Log($"HID device removal detected: {devPath}");

        if (_containerMap.TryGetValue(devPath, out var containerId))
        {
            // TEST HARNESS: Extract and log device information BEFORE disposal
            var hidppDevices = _deviceMap[containerId];
            var deviceCollection = hidppDevices.DeviceCollection;

            DiagnosticLogger.Log($"Container ID: {containerId}");
            DiagnosticLogger.Log($"Device count in container: {deviceCollection.Count}");

            // Log each device in the container
            foreach (var (deviceIdx, device) in deviceCollection)
            {
                string logMessage = $"Removing device - " +
                                  $"Identifier: {device.Identifier}, " +
                                  $"Name: {device.DeviceName}, " +
                                  $"Type: {(DeviceType)device.DeviceType}, " +
                                  $"Index: {device.DeviceIdx}";
                DiagnosticLogger.Log(logMessage);
            }

            // Send offline notifications to UI before disposal
            foreach (var (deviceIdx, device) in deviceCollection)
            {
                // Only send for devices that completed initialization
                if (!string.IsNullOrEmpty(device.Identifier))
                {
                    // FIX: Check if device is active on another receiver (e.g., wireless when wired is unplugged)
                    // This prevents mode-switch keyboards from showing offline when switching wired→wireless
                    bool hasAlternativeSource = _deviceMap
                        .Where(kvp => kvp.Key != containerId) // Ignore the container being removed
                        .SelectMany(kvp => kvp.Value.DeviceCollection.Values)
                        .Any(d => d.Identifier == device.Identifier && d.IsOnline && !d.Disposed);

                    if (hasAlternativeSource)
                    {
                        DiagnosticLogger.Log($"[{device.DeviceName}] Device removed from {containerId:D} but active on alternative source - skipping offline notification");

                        // Proactively update battery on the alternative source to refresh UI immediately
                        // instead of waiting for the next polling cycle (default 600s)
                        var altDevice = _deviceMap
                            .Where(kvp => kvp.Key != containerId)
                            .SelectMany(kvp => kvp.Value.DeviceCollection.Values)
                            .FirstOrDefault(d => d.Identifier == device.Identifier && d.IsOnline && !d.Disposed);

                        if (altDevice != null)
                        {
                            // Capture device reference for async operation (avoid unsafe context in lambda)
                            var deviceToUpdate = altDevice;
                            var deviceName = device.DeviceName;

                            // Trigger battery update asynchronously (via helper method to avoid unsafe context)
                            _ = Task.Run(() => TriggerModeSwitchBatteryUpdate(deviceToUpdate, deviceName));
                        }

                        // Skip sending offline notification - device is still connected via alternative path
                        continue;
                    }

                    // Device has no alternative source - send offline notification
                    HidppManagerContext.Instance.SignalDeviceEvent(
                        IPCMessageType.UPDATE,
                        new UpdateMessage(
                            deviceId: device.Identifier,
                            batteryPercentage: -1,  // Convention: -1 = offline
                            powerSupplyStatus: PowerSupplyStatus.UNKNOWN,
                            batteryMVolt: 0,
                            updateTime: DateTimeOffset.Now,
                            mileage: -1
                        )
                    );
                    DiagnosticLogger.Log($"[{device.DeviceName}] Receiver removal - offline notification sent to UI");
                }
            }

            // Original disposal logic
            _deviceMap[containerId].Dispose();
            _deviceMap.Remove(containerId);
            _containerMap.Remove(devPath);

            // TEST HARNESS: Confirm cleanup completed
            DiagnosticLogger.Log($"Device removal complete - Path: {devPath}, Container: {containerId}");
        }
        return 0;
    }
    public void SignalDeviceEvent(IPCMessageType messageType, IPCMessage message)
    {
        HidppDeviceEvent?.Invoke(messageType, message);
    }

    /// <summary>
    /// Trigger battery update on alternative device source after mode-switch.
    /// This ensures UI refreshes immediately instead of waiting for the next polling cycle.
    /// </summary>
    private static async Task TriggerModeSwitchBatteryUpdate(HidppDevice device, string deviceName)
    {
        // Brief delay to allow device firmware to complete mode-switch handover
        await Task.Delay(1000);
        await device.UpdateBattery(forceIpcUpdate: true);
        DiagnosticLogger.Log($"[{deviceName}] Triggered battery update on alternative source after mode-switch");
    }

    /// <summary>
    /// Track USB device arrival for mode-switch detection.
    /// Called when a new USB device is detected.
    /// </summary>
    private void TrackUsbArrival(ushort productId, Guid containerId, string devicePath)
    {
        lock (_arrivalLock)
        {
            var now = DateTimeOffset.Now;

            // Clean up old entries outside tracking window
            _recentArrivals.RemoveAll(r => now - r.Timestamp > ArrivalTrackingWindow);

            // Add new arrival
            _recentArrivals.Add(new UsbArrivalRecord(now, productId, containerId, devicePath));

            DiagnosticLogger.Log($"[Mode-Switch] Tracked USB arrival - PID: 0x{productId:X04}, Container: {containerId}");
        }
    }

    /// <summary>
    /// Check if a new USB device appeared recently (within 5 seconds).
    /// This indicates a potential mode switch from wireless to wired.
    /// </summary>
    /// <returns>True if a new device appeared recently and might indicate wired mode</returns>
    public bool CheckForRecentUsbArrival(out ushort newProductId)
    {
        lock (_arrivalLock)
        {
            var now = DateTimeOffset.Now;

            // Look for arrivals in the last 5 seconds
            var recentArrival = _recentArrivals
                .Where(r => now - r.Timestamp <= ArrivalTrackingWindow)
                .OrderByDescending(r => r.Timestamp)
                .FirstOrDefault();

            if (recentArrival != null)
            {
                newProductId = recentArrival.ProductId;
                DiagnosticLogger.Log($"[Mode-Switch] Recent USB arrival detected - " +
                    $"PID: 0x{newProductId:X04}, " +
                    $"Age: {(now - recentArrival.Timestamp).TotalSeconds:F1}s");
                return true;
            }

            newProductId = 0;
            return false;
        }
    }

}
