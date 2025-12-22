using static LGSTrayHID.HidApi.HidApi;
using static LGSTrayHID.HidApi.HidApiWinApi;
using static LGSTrayHID.HidApi.HidApiHotPlug;
using LGSTrayHID.HidApi;
using System.Collections.Concurrent;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;

namespace LGSTrayHID;

public sealed class HidppManagerContext
{
    public static readonly HidppManagerContext _instance = new();
    public static HidppManagerContext Instance => _instance;

    private readonly Dictionary<string, Guid> _containerMap = [];
    private readonly Dictionary<Guid, HidppReceiver> _deviceMap = [];
    private readonly BlockingCollection<HidDeviceInfo> _deviceQueue = [];

    public delegate void HidppDeviceEventHandler(IPCMessageType messageType, IPCMessage message);

    public event HidppDeviceEventHandler? HidppDeviceEvent;

    private HidppManagerContext() {  }

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

        await hidppReceiver.SetUp(messageType, dev);

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

            // Original disposal logic
            _deviceMap[containerId].Dispose();
            _deviceMap.Remove(containerId);
            _containerMap.Remove(devPath);

            // TEST HARNESS: Confirm cleanup completed
            DiagnosticLogger.Log($"Device removal complete - Path: {devPath}, Container: {containerId}");
        }
        else
        {
            // TEST HARNESS: Log when device path not found in container map
            DiagnosticLogger.LogWarning($"Device removal for unknown path: {devPath}");
        }

        return 0;
    }
    public void SignalDeviceEvent(IPCMessageType messageType, IPCMessage message)
    {
        HidppDeviceEvent?.Invoke(messageType, message);
    }

}
