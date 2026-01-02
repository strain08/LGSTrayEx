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
    private const int PING_TIMEOUT = 2000;

    public DeviceEnumerator(HidppReceiver parent, DeviceLifecycleManager lifecycleManager)
    {
        _parent = parent;
        _lifecycleManager = lifecycleManager;
    }

    /// <summary>
    /// Enumerates devices using two strategies:
    /// 1. Query receiver for device count + force announcements (waits for announcements or timeout).
    /// 2. If not all devices announced, fallback to ping enumeration (indices 1-6).
    /// </summary>
    public async Task EnumerateDevicesAsync()
    {
        byte numDevicesFound = await QueryAndAnnounceAsync();

        // Trigger fallback if no devices were reported or if not all devices announced
        if (numDevicesFound == 0)
        {
            DiagnosticLogger.Log("No devices reported by receiver, starting fallback ping enumeration.");
            await Task.Delay(FALLBACK_DELAY);
            await FallbackPingEnumerationAsync();
        }
        else if (_lifecycleManager.Count < numDevicesFound)
        {
            DiagnosticLogger.Log($"Only {_lifecycleManager.Count}/{numDevicesFound} devices announced, starting fallback ping enumeration.");
            await Task.Delay(FALLBACK_DELAY);
            await FallbackPingEnumerationAsync();
        }
        else
        {
            DiagnosticLogger.Log($"All {numDevicesFound} devices successfully enumerated via announcements.");
        }
    }

    /// <summary>
    /// Strategy 1: Query receiver for connected device count and force announcements.
    /// Waits for expected devices to announce or timeout.
    /// Returns the number of devices reported by the receiver.
    /// </summary>
    private async Task<byte> QueryAndAnnounceAsync()
    {
        try
        {
            // Query receiver for number of connected devices
            byte[] response = await _parent.WriteRead10(
                _parent.DevShort,
                Hidpp10Commands.QueryDeviceCount(),
                backoffStrategy: GlobalSettings.ReceiverInitBackoff
            );

            DiagnosticLogger.Log($"[QueryDeviceCount] Response: {BitConverter.ToString(response)} (Length={response.Length})");

            byte numDevicesFound = 0;
            if (response.Length >= 6 &&
                (response[2] == ReceiverCommand.QUERY_DEVICE_COUNT) &&
                (response[3] == ReceiverCommand.SUB_COMMAND))
            {
                numDevicesFound = response[5];
            }
            else if (response.Length > 0)
            {
                DiagnosticLogger.Log($"[QueryDeviceCount] Parser rejected: byte[2]={response[2]:X2} (expected {ReceiverCommand.QUERY_DEVICE_COUNT:X2}), byte[3]={response[3]:X2} (expected {ReceiverCommand.SUB_COMMAND:X2})");
            }

            if (numDevicesFound > 0)
            {
                // Setup event-driven wait for announcements
                // CRITICAL: RunContinuationsAsynchronously prevents deadlock when TrySetResult is called
                // from within CreateDevice (which happens when Device 2 creation completes enumeration)
                var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _lifecycleManager.SetExpectedDeviceCount(numDevicesFound, completion);

                // Force connected devices to announce themselves (0x41 messages)
                await _parent.WriteRead10(
                    _parent.DevShort,
                    Hidpp10Commands.ForceDeviceAnnounce(),
                    1000
                );

                // Wait for announcements or timeout (5 seconds)
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(completion.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    DiagnosticLogger.Log($"Announcement timeout: {_lifecycleManager.Count}/{numDevicesFound} devices announced");
                }
                else
                {
                    DiagnosticLogger.Log($"All {numDevicesFound} devices announced successfully");
                }
            }

            return numDevicesFound;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"QueryAndAnnounceAsync failed: {ex.GetType().Name} - {ex.Message}");
            // Return 0 to trigger fallback enumeration
            return 0;
        }
    }

    /// <summary>
    /// Strategy 2: Fallback ping-based enumeration.
    /// Pings device indices 1-6 with extended timeout (20 seconds) to wake sleeping devices.
    /// Only initializes devices that were discovered by fallback (not Device ON events).
    /// </summary>
    private async Task FallbackPingEnumerationAsync()
    {
        DiagnosticLogger.Log("Fallback enumeration: pinging device indices 1-6");

        var devicesToInitialize = new List<HidppDevice>();

        // Ping device indices 1-6 (standard receiver slots)
        for (byte i = 1; i <= 6; i++)
        {
            try
            {                
                bool pingSuccess = await _parent.Ping20(i, PING_TIMEOUT, ignoreHIDPP10: false);            

                if (pingSuccess)
                {
                    // Check if device already exists (from Device ON event)
                    if (!_lifecycleManager.TryGetDevice(i, out _))
                    {
                        DiagnosticLogger.Log($"Fallback discovered new device at index {i}");
                        var device = _lifecycleManager.CreateDevice(i);
                        devicesToInitialize.Add(device);
                    }
                    else
                    {
                        DiagnosticLogger.Log($"Device {i} already exists (from Device ON event), skipping fallback init");
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError($"Ping to device index {i} failed: {ex.Message} {ex.Source}");
                break;
            }
        }

        // Initialize ONLY devices discovered by fallback (not Device ON events)
        foreach (var device in devicesToInitialize)
        {
            try
            {
                DiagnosticLogger.Log($"Starting fallback initialization for device {device.DeviceIdx}");
                await device.InitAsync();
                DiagnosticLogger.Log($"Completed fallback initialization for device {device.DeviceIdx}");
            }
            catch (Exception ex)
            {
                var stackFirstLine = ex.StackTrace?.Split('\n')[0].Trim() ?? "No stack trace";
                DiagnosticLogger.LogError($"Device {device.DeviceIdx} fallback initialization failed: " +
                                         $"{ex.GetType().Name} - {ex.Message} | {stackFirstLine}");
            }
        }
    }
}
