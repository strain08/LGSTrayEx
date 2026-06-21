using LGSTrayHID.Centurion;
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
    private readonly Dictionary<string, CenturionDevice> _centurionMap = [];
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


    // Logitech webcams (PID range from docs/usb.ids.txt) can lock up during HID++ probing,
    // so skip the descriptor probe for them. Matches Solaar's webcam exclusion. VID is already
    // guaranteed 0x046D by the hotplug filter.
    private static bool IsExcludedFromProbe(ushort productId) => 
        (productId is >= 0x0800 and <= 0x09FF) || // Webcams
        (productId is 0x0A5B or 0x0A51);          // G933/G633 which can be finicky

    private async Task InitDevice(HidDeviceInfo deviceInfo)
    {
        var messageType = deviceInfo.GetHidppMessageType();
        string devPath = deviceInfo.GetPath();
        HidppReportId reportId = HidppReportId.None;

        // Resolve devices the usage page doesn't classify by reading the Windows HID descriptor
        // (no device I/O): G733-class headsets on a non-FF00/FF43 page, and Centurion collections
        // (usage page 0xFFA0) whose specific report ID (0x50/0x51) is needed up front. The probe is
        // internally guarded, so any failure returns NONE and we fall through to the skip path below.
        if (messageType == HidppMessageType.NONE && !IsExcludedFromProbe(deviceInfo.ProductId))
        {
            // ProbeFromDescriptor logs the full declared report-ID/size picture for the collection,
            // match or not, so we don't add a second line here.
            var probe = HidppReportProbe.ProbeFromDescriptor(devPath);
            messageType = probe.MessageType;
            reportId = probe.ReportId;
        }

        switch (messageType)
        {
            case HidppMessageType.LONG:
            case HidppMessageType.SHORT:
                DiagnosticLogger.Log($"Initializing HID++ device...");

                HidDevicePtr hidppDev = HidOpenPath(ref deviceInfo);
                _ = HidWinApiGetContainerId(hidppDev, out Guid containerId);

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
                    hidppReceiver = new();
                    _deviceMap[containerId] = hidppReceiver;
                    _containerMap[devPath] = containerId;
                    DiagnosticLogger.Log($"New container created - Path: {devPath}, Container: {containerId}");
                }
                else
                {
                    DiagnosticLogger.Log($"Existing container found - Path: {devPath}, Container: {containerId}");
                }

                await hidppReceiver.SetUp(messageType, hidppDev, deviceInfo.UsagePage, isWiredModeDevice);
                return;

            case HidppMessageType.CENTURION:
                DiagnosticLogger.Log($"[Centurion] Detected Centurion interface 0x{deviceInfo.UsagePage:X4}: {devPath}");
                HidDevicePtr centDev = HidOpenPath(ref deviceInfo);
                if (centDev == 0)
                {
                    DiagnosticLogger.LogError($"[Centurion] Failed to open HID device: {devPath}");
                    return;
                }
                string? productName = deviceInfo.GetProductString();
                var centurion = new CenturionDevice(centDev, deviceInfo.UsagePage, deviceInfo.ProductId, reportId, productName);
                _centurionMap[devPath] = centurion;
                _ = Task.Run(() => centurion.InitAsync());
                return;

            default:
                DiagnosticLogger.Log($"Skipping device with unsupported message type: {messageType} " +
                    $"(UsagePage: 0x{deviceInfo.UsagePage:X04}, Usage: 0x{deviceInfo.Usage:X04}, Path: {devPath})");
                return;
        }
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
        DiagnosticLogger.Log($"HID device removal detected: {devPath}");

        if (_centurionMap.TryGetValue(devPath, out var centurionDevice))
        {
            DiagnosticLogger.Log($"[Centurion] Removing device: {devPath}");
            centurionDevice.Dispose();
            _centurionMap.Remove(devPath);
            return 0;
        }

        if (!_containerMap.TryGetValue(devPath, out var containerId)) return 0;

        // Extract and log device information BEFORE disposal
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
            if (string.IsNullOrEmpty(device.Identifier)) continue;

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

                if (altDevice is null) continue;

                // Capture device reference for async operation (avoid unsafe context in lambda)
                var deviceToUpdate = altDevice;
                var deviceName = device.DeviceName;

                // Trigger battery update asynchronously (via helper method to avoid unsafe context)
                _ = Task.Run(() => TriggerModeSwitchBatteryUpdate(deviceToUpdate, deviceName));

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

        // Original disposal logic
        _deviceMap[containerId].Dispose();
        _deviceMap.Remove(containerId);
        _containerMap.Remove(devPath);

        // Confirm cleanup completed
        DiagnosticLogger.Log($"Device removal complete - Path: {devPath}, Container: {containerId}");

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

    private static bool IsReceiverPid(ushort productId)
    {
        ushort[] receiverPids =
        [
            0x0A66 , // G533
            0x0A87 , // G935
            0x0ABA , // PRO X Wireless
            0x0AB5 , // G733
            0x0AFE , // G733 New
            0x0AC4 , // G535
            0x0B18   // G522 Centurion dongle                            
        ];

        // Standard receivers (Unifying, Bolt, Nano)
        if (productId is >= 0xC500 and <= 0xC5FF) return true;
        // Gaming headset dongles (2.0-only bridges)
        return receiverPids.Contains(productId);        
    }

    /// <summary>
    /// Track USB device arrival for mode-switch detection.
    /// Called when a new USB device is detected.
    /// </summary>
    private void TrackUsbArrival(ushort productId, Guid containerId, string devicePath)
    {
        // Only track direct wired device arrivals. Wireless receivers (including 
        // 2.0-only bridges like G733 dongles) are not mode-switch candidates.
        if (IsReceiverPid(productId)) return;

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
