using LGSTrayHID.Lifecycle;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LGSTrayHID.Routing;

/// <summary>
/// Handles device announcement events (device ON/OFF).
/// Isolates bit interpretation logic and device lifecycle orchestration.
/// </summary>
public class DeviceAnnouncementHandler
{
    private readonly DeviceLifecycleManager _lifecycleManager;
    private readonly SemaphoreSlim _initSemaphore;

    public DeviceAnnouncementHandler(
        DeviceLifecycleManager lifecycleManager,
        SemaphoreSlim initSemaphore)
    {
        _lifecycleManager = lifecycleManager;
        _initSemaphore = initSemaphore;
    }

    /// <summary>
    /// Handles device announcement messages (0x41).
    /// Determines if device is ON or OFF based on bit 6 of buffer[4].
    /// </summary>
    /// <param name="buffer">Raw HID++ announcement message buffer</param>
    public async Task HandleAnnouncementAsync(byte[] buffer)
    {
        byte deviceIdx = buffer[1];
        bool isDeviceOn = (buffer[4] & 0x40) == 0;  // Bit 6 clear = ON, set = OFF

        if (isDeviceOn)
        {
            await HandleDeviceOnAsync(deviceIdx, buffer);
        }
        else
        {
            await HandleDeviceOffAsync(deviceIdx, buffer);
        }
    }

    /// <summary>
    /// Handles device ON announcement.
    /// Creates device instance and spawns initialization thread with sequential enforcement.
    /// </summary>
    private async Task HandleDeviceOnAsync(byte deviceIdx, byte[] buffer)
    {
        DiagnosticLogger.Log($"[Device ON Event] Index: {deviceIdx}, " +
                            $"Params: [0x{buffer[3]:X02} 0x{buffer[4]:X02} 0x{buffer[5]:X02} 0x{buffer[6]:X02}]");

        HidppDevice device = _lifecycleManager.CreateDevice(deviceIdx);

        // Spawn initialization thread with sequential enforcement
        new Thread(async () =>
        {
            try
            {
                await Task.Delay(1000); // Device stabilization delay

                // Wait for previous device initialization to complete (sequential init)
                await _initSemaphore.WaitAsync();
                try
                {
                    DiagnosticLogger.Log($"Starting initialization for device {deviceIdx}");
                    await device.InitAsync();
                    DiagnosticLogger.Log($"Completed initialization for device {deviceIdx}");
                }
                finally
                {
                    _initSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError($"Device {deviceIdx} initialization failed: {ex.Message}");
            }
        }).Start();

        await Task.CompletedTask; // Suppress async warning
    }

    /// <summary>
    /// Handles device OFF announcement.
    /// Sends UpdateMessage with offline status to UI via IPC.
    /// Device remains in collection (as per user requirement).
    /// </summary>
    private async Task HandleDeviceOffAsync(byte deviceIdx, byte[] buffer)
    {
        string deviceName = "Unknown";
        string deviceId = "";

        if (_lifecycleManager.TryGetDevice(deviceIdx, out HidppDevice? device))
        {
            deviceName = device!.DeviceName;
            deviceId = device.Identifier;
        }

        DiagnosticLogger.Log($"[Device OFF Event] Index: {deviceIdx}, " +
                            $"Name: {deviceName}, " +
                            $"Params: [0x{buffer[3]:X02} 0x{buffer[4]:X02} 0x{buffer[5]:X02} 0x{buffer[6]:X02}]");

        if (!string.IsNullOrEmpty(deviceId))
        {
            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.UPDATE,
                new UpdateMessage(
                    deviceId: deviceId,
                    batteryPercentage: -1,  // Convention: -1 = offline/unknown
                    powerSupplyStatus: PowerSupplyStatus.POWER_SUPPLY_STATUS_UNKNOWN,
                    batteryMVolt: 0,
                    updateTime: DateTimeOffset.Now,
                    mileage: -1
                )
            );
            DiagnosticLogger.Log($"[{deviceName}] Device offline notification sent to UI");
        }

        await Task.CompletedTask; // Suppress async warning
    }
}
