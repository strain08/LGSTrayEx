using LGSTrayHID.Lifecycle;
using LGSTrayHID.Protocol;
using LGSTrayPrimitives;

namespace LGSTrayHID.Initialization;

/// <summary>
/// Orchestrates device discovery strategies.
/// Strategy 1: Query receiver + force announce (primary).
/// Strategy 2: Fallback ping 1-6 (if primary fails).
/// </summary>
public class DeviceEnumerator
{
    private readonly HidppReceiver _parent;
    private readonly DeviceLifecycleManager _lifecycleManager;
    private const int FALLBACK_DELAY = 2000;
    private const int PING_ENUMERATE_DELAY = 20_000;

    public DeviceEnumerator(HidppReceiver parent, DeviceLifecycleManager lifecycleManager)
    {
        _parent = parent;
        _lifecycleManager = lifecycleManager;
    }

    /// <summary>
    /// Enumerates devices using two strategies:
    /// 1. Query receiver for device count + force announcements (waits TASK_DELAY).
    /// 2. If no devices found, fallback to ping enumeration (indices 1-6).
    /// </summary>
    public async Task EnumerateDevicesAsync()
    {
        //force announcements
        await _parent.WriteRead10(
            _parent.DevShort,
            Hidpp10Commands.ForceDeviceAnnounce(),
            1000
        );

        byte numDevicesFound = await QueryAndAnnounceAsync();

        if (numDevicesFound > 0)
        {
            DiagnosticLogger.Log($"Devices reported by receiver after query: {numDevicesFound}");
            return;
        }
        else
        {
            DiagnosticLogger.Log("No devices reported by receiver after query.");
            await Task.Delay(FALLBACK_DELAY);
            await FallbackPingEnumerationAsync();
        }

        //Strategy 2: Fallback ping-based enumeration(if no devices found)
        //if (_lifecycleManager.Count == 0)
        //{
        //    await FallbackPingEnumerationAsync();
        //}
    }

    /// <summary>
    /// Strategy 1: Query receiver for connected device count and force announcements.
    /// Returns the number of devices reported by the receiver.
    /// </summary>
    private async Task<byte> QueryAndAnnounceAsync()
    {
        // Query receiver for number of connected devices
        byte[] response = await _parent.WriteRead10(
            _parent.DevShort,
            Hidpp10Commands.QueryDeviceCount(),
            1000
        );

        byte numDevicesFound = 0;
        if ((response[2] == ReceiverCommand.QUERY_DEVICE_COUNT) &&
            (response[3] == ReceiverCommand.SUB_COMMAND))
        {
            numDevicesFound = response[5];
        }

        if (numDevicesFound > 0)
        {
            // Force connected devices to announce themselves (0x41 messages)
            await _parent.WriteRead10(
                _parent.DevShort,
                Hidpp10Commands.ForceDeviceAnnounce(),
                1000
            );
        }

        return numDevicesFound;
    }

    /// <summary>
    /// Strategy 2: Fallback ping-based enumeration.
    /// Pings device indices 1-6 with extended timeout (5 seconds) to wake sleeping devices.
    /// Initializes all discovered devices sequentially.
    /// </summary>
    private async Task FallbackPingEnumerationAsync()
    {
        DiagnosticLogger.Log("Fallback enumeration: pinging device indices 1-6");

        // Ping device indices 1-6 (standard receiver slots)
        for (byte i = 1; i <= 6; i++)
        {
            bool pingSuccess = await _parent.Ping20(i, PING_ENUMERATE_DELAY, ignoreHIDPP10: false);

            if (pingSuccess)
            {
                _lifecycleManager.CreateDevice(i);
            }
        }

        // Initialize all discovered devices sequentially
        foreach ((_, var device) in _lifecycleManager.Devices)
        {
            try
            {
                DiagnosticLogger.Log($"Starting fallback initialization for device {device.DeviceIdx}");
                await device.InitAsync();
                DiagnosticLogger.Log($"Completed fallback initialization for device {device.DeviceIdx}");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError($"Device {device.DeviceIdx} fallback initialization failed: {ex.Message}");
            }
        }
    }
}
