using LGSTrayHID.Lifecycle;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;

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
        bool isDeviceOff = (buffer[4] & 0x40) != 0;  // Bit 6 clear = ON, set = OFF

        try
        {
            if (!isDeviceOff)
            {
                await HandleDeviceOnAsync(deviceIdx, buffer);
                DiagnosticLogger.Log($"[HandleAnnouncement] Device {deviceIdx} ON handler completed");
            }
            else
            {
                await HandleDeviceOffAsync(deviceIdx, buffer);
                DiagnosticLogger.Log($"[HandleAnnouncement] Device {deviceIdx} OFF handler completed");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[HandleAnnouncement] Device {deviceIdx} announcement handler exception: {ex.GetType().Name} - {ex.Message}");
            DiagnosticLogger.LogError($"[HandleAnnouncement] Stack trace: {ex.StackTrace}");
            throw;
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

        // Diagnostic: Check device state before proceeding
        bool deviceExists = _lifecycleManager.TryGetDevice(deviceIdx, out HidppDevice? existingDevice);
        if (deviceExists)
        {
            DiagnosticLogger.Log($"[Device {deviceIdx}] Existing device found - " +
                                $"Identifier: {existingDevice?.Identifier ?? "null"}, " +
                                $"IsOnline: {existingDevice?.IsOnline}, " +
                                $"Disposed: {existingDevice?.Disposed}");
        }
        else
        {
            DiagnosticLogger.Log($"[Device {deviceIdx}] No existing device found, will create new device");
        }

        // Check if device is already initialized and healthy
        if (_lifecycleManager.IsDeviceInitialized(deviceIdx))
        {
            DiagnosticLogger.Log($"[Device {deviceIdx}] Device ON event ignored (already initialized and online)");
            return;
        }

        // Check if we should proceed with initialization (prevents duplicate init)
        if (!_lifecycleManager.ShouldInitialize(deviceIdx))
        {
            DiagnosticLogger.Log($"[Device {deviceIdx}] Device ON event ignored (duplicate/cooldown)");
            return;
        }

        DiagnosticLogger.Log($"[Device {deviceIdx}] Guard clauses passed, proceeding with initialization");

        HidppDevice device;
        try
        {
            DiagnosticLogger.Log($"[Device {deviceIdx}] Calling CreateDevice...");
            device = _lifecycleManager.CreateDevice(deviceIdx);
            DiagnosticLogger.Log($"[Device {deviceIdx}] CreateDevice completed, calling NotifyDeviceOn...");

            device.NotifyDeviceOn(); // Set device ON timestamp
            DiagnosticLogger.Log($"[Device {deviceIdx}] NotifyDeviceOn completed");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[Device {deviceIdx}] Exception during device creation/notification: {ex.GetType().Name} - {ex.Message}");
            DiagnosticLogger.LogError($"[Device {deviceIdx}] Stack trace: {ex.StackTrace}");
            return;
        }

        DiagnosticLogger.Log($"[Device {deviceIdx}] Creating Task.Run for initialization...");

        // Fire-and-forget initialization task with proper async handling
        var task = Task.Run(async () =>
        {
            try
            {
                DiagnosticLogger.Log($"[Device {deviceIdx}] Initialization task started, waiting for stabilization...");
                await Task.Delay(1000); // Device stabilization delay

                // Check if device still valid after delay (may have been disposed during resume)
                if (device.Disposed)
                {
                    DiagnosticLogger.LogWarning($"[Device {deviceIdx}] Device disposed during stabilization delay, aborting initialization");
                    return;
                }

                // Wait for previous device initialization to complete (sequential init)
                DiagnosticLogger.Log($"[Device {deviceIdx}] Waiting for semaphore...");
                await _initSemaphore.WaitAsync();
                try
                {
                    // Final check before expensive initialization
                    if (device.Disposed)
                    {
                        DiagnosticLogger.LogWarning($"[Device {deviceIdx}] Device disposed while waiting for semaphore, aborting initialization");
                        return;
                    }

                    DiagnosticLogger.Log($"[Device {deviceIdx}] Semaphore acquired, starting initialization");
                    await device.InitAsync();
                    DiagnosticLogger.Log($"[Device {deviceIdx}] Initialization completed successfully");
                }
                finally
                {
                    _initSemaphore.Release();
                    DiagnosticLogger.Log($"[Device {deviceIdx}] Semaphore released");
                }
            }
            catch (Exception ex)
            {
                // Log with full stack trace for diagnosis
                DiagnosticLogger.LogError($"[Device {deviceIdx}] Initialization failed: {ex.GetType().Name} - {ex.Message}");
                if (ex.StackTrace != null)
                {
                    DiagnosticLogger.LogError($"[Device {deviceIdx}] Stack trace: {ex.StackTrace}");
                }
            }
        });

        DiagnosticLogger.Log($"[Device {deviceIdx}] Task.Run created successfully, task ID: {task.Id}, status: {task.Status}");

        DiagnosticLogger.Log($"[Device {deviceIdx}] HandleDeviceOnAsync completing...");
        await Task.CompletedTask; // Suppress async warning
    }

    /// <summary>
    /// Handles device OFF announcement.
    /// Detects mode switch (wireless→wired) and sends appropriate UpdateMessage to UI via IPC.
    /// Device remains in collection (as per user requirement).
    /// </summary>
    private async Task HandleDeviceOffAsync(byte deviceIdx, byte[] buffer)
    {
        string deviceName = "Unknown";
        string deviceIdentifier = "";

        if (_lifecycleManager.TryGetDevice(deviceIdx, out HidppDevice? device))
        {
            deviceName = device?.DeviceName ?? "";
            deviceIdentifier = device?.Identifier ?? "";
            // Mark device as offline and cancel any ongoing polling tasks
            device?.SetOffline();
            device?.CancelPooling();
        }

        string eventType = string.IsNullOrEmpty(deviceIdentifier) ? "Phantom OFF Event (ignored)" : "Device OFF Event";
        DiagnosticLogger.Log($"[{eventType}] Index: {deviceIdx}, " +
                            $"Name: {deviceName}, " +
                            $"Params: [0x{buffer[3]:X02} 0x{buffer[4]:X02} 0x{buffer[5]:X02} 0x{buffer[6]:X02}]");

        if (!string.IsNullOrEmpty(deviceIdentifier))
        {
            // Check if a new USB device appeared recently (mode switch detection)
            bool isModeSwitch = HidppManagerContext.Instance.CheckForRecentUsbArrival(out ushort newProductId);

            if (isModeSwitch)
            {
                DiagnosticLogger.Log($"[{deviceName}] MODE SWITCH DETECTED - " +
                    $"Wireless disconnected, new USB device PID 0x{newProductId:X04} appeared recently. " +
                    $"Likely switched to wired mode (charging).");
            }

            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.UPDATE,
                new UpdateMessage(
                    deviceId: deviceIdentifier,
                    batteryPercentage: -1,  // Convention: -1 = offline/unknown
                    powerSupplyStatus: isModeSwitch
                        ? PowerSupplyStatus.CHARGING
                        : PowerSupplyStatus.UNKNOWN,
                    batteryMVolt: 0,
                    updateTime: DateTimeOffset.Now,
                    mileage: -1,
                    isWiredMode: isModeSwitch  // Flag indicating wired mode
                )
            );

            string statusMessage = isModeSwitch
                ? $"[{deviceName}] Wired mode notification sent to UI"
                : $"[{deviceName}] Device offline notification sent to UI";
            DiagnosticLogger.Log(statusMessage);
        }

        await Task.CompletedTask; // Suppress async warning
    }
}
