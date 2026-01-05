using LGSTrayPrimitives;
using LGSTrayHID.Protocol;

namespace LGSTrayHID.Lifecycle;

/// <summary>
/// Manages the lifecycle of HID++ devices, centralizing device collection operations.
/// Prevents direct dictionary mutation and provides a clean abstraction for device management.
/// </summary>
public class DeviceLifecycleManager
{
    private readonly Dictionary<ushort, HidppDevice> _devices = [];
    private readonly HidppReceiver _parent;

    // Track device initialization timestamps to prevent duplicate init
    private readonly Dictionary<byte, DateTimeOffset> _lastInitTime = new();
    private static readonly TimeSpan InitCooldown = TimeSpan.FromSeconds(3);

    // Configuration settings
    private readonly bool _keepPollingWithEvents;
    private readonly int _batteryEventDelaySeconds;

    // Enumeration completion tracking
    private TaskCompletionSource<int>? _enumerationCompletion;
    private int _expectedDeviceCount;

    public DeviceLifecycleManager(HidppReceiver parent, bool keepPollingWithEvents, int batteryEventDelaySeconds)
    {
        _parent = parent;
        _keepPollingWithEvents = keepPollingWithEvents;
        _batteryEventDelaySeconds = batteryEventDelaySeconds;
    }

    /// <summary>
    /// Read-only view of the device collection.
    /// </summary>
    public IReadOnlyDictionary<ushort, HidppDevice> Devices => _devices;

    /// <summary>
    /// Creates a new HidppDevice instance and adds it to the collection.
    /// If a device with the same index already exists, it will be replaced.
    /// </summary>
    /// <param name="deviceIdx">Device index (1-6 for standard receivers)</param>
    /// <param name="isWiredModeDevice">True if this is a wired mode device (fast-track init)</param>
    /// <returns>The newly created HidppDevice instance</returns>
    public HidppDevice CreateDevice(byte deviceIdx, bool isWiredModeDevice = false)
    {
        var device = new HidppDevice(_parent, deviceIdx, _keepPollingWithEvents, _batteryEventDelaySeconds, isWiredModeDevice);

        lock (_devices)
        {
            // Check if device already exists (replacement scenario)
            bool isReplacement = _devices.ContainsKey(deviceIdx);
            if (isReplacement)
            {
                DiagnosticLogger.Log($"[Device {deviceIdx}] Replacing existing device in collection");
            }

            _devices[deviceIdx] = device;

            // Check if we've reached expected count during enumeration
            if (_enumerationCompletion != null && Count >= _expectedDeviceCount)
            {
                DiagnosticLogger.Log($"[DeviceLifecycle] Enumeration complete - " +
                                    $"Reached expected count {_expectedDeviceCount} " +
                                    $"(triggered by device {deviceIdx} creation)");
                _enumerationCompletion.TrySetResult(Count);
                _enumerationCompletion = null;
            }
        }

        return device;
    }

    /// <summary>
    /// Attempts to retrieve a device by its index.
    /// </summary>
    /// <param name="deviceIdx">Device index to look up</param>
    /// <param name="device">The found device, or null if not found</param>
    /// <returns>True if device was found, false otherwise</returns>
    public bool TryGetDevice(byte deviceIdx, out HidppDevice? device) => _devices.TryGetValue(deviceIdx, out device);

    /// <summary>
    /// Check if a device initialization should proceed.
    /// Prevents duplicate initialization within cooldown window.
    /// </summary>
    /// <param name="deviceIdx">Device index</param>
    /// <returns>True if initialization should proceed, false if duplicate/too soon</returns>
    public bool ShouldInitialize(byte deviceIdx)
    {
        lock (_devices)
        {
            var now = DateTimeOffset.Now;

            // Check if we've initialized this device recently
            if (_lastInitTime.TryGetValue(deviceIdx, out var lastInit))
            {
                var timeSinceInit = now - lastInit;
                if (timeSinceInit < InitCooldown)
                {
                    DiagnosticLogger.Log($"[Device {deviceIdx}] Skipping duplicate initialization " +
                                       $"(last init {timeSinceInit.TotalSeconds:F1}s ago, cooldown {InitCooldown.TotalSeconds}s)");
                    return false;
                }
            }

            // Record this initialization attempt
            _lastInitTime[deviceIdx] = now;
            return true;
        }
    }

    /// <summary>
    /// Checks if a device at the given index is already initialized and healthy.
    /// A device is considered initialized if it has a valid identifier, is not disposed, and is online.
    /// </summary>
    /// <param name="deviceIdx">Device index to check</param>
    /// <returns>True if device exists, is initialized, not disposed, and online</returns>
    public bool IsDeviceInitialized(byte deviceIdx)
    {
        lock (_devices)
        {
            if (!_devices.TryGetValue(deviceIdx, out var device))
                return false;

            // Check if device is initialized (has identifier), not disposed, and online
            // IsOnline=false indicates device went offline (OFF event) and needs reinitialization
            // Note: Polling may be cancelled due to battery events (keepPollingWithEvents=false),
            // but device is still online and doesn't need reinitialization
            return !string.IsNullOrEmpty(device.Identifier)
                && !device.Disposed
                && device.IsOnline;
        }
    }

    /// <summary>
    /// Gets the current number of devices in the collection.
    /// </summary>
    public int Count => _devices.Count;

    /// <summary>
    /// Sets the expected device count for enumeration and the completion source to signal.
    /// Used for event-driven device enumeration synchronization.
    /// </summary>
    /// <param name="count">Expected number of devices to announce</param>
    /// <param name="completion">TaskCompletionSource to signal when expected count is reached</param>
    public void SetExpectedDeviceCount(int count, TaskCompletionSource<int> completion)
    {
        lock (_devices)
        {
            _expectedDeviceCount = count;
            _enumerationCompletion = completion;

            // Check if we've already reached the count (race condition edge case)
            if (Count >= _expectedDeviceCount)
            {
                _enumerationCompletion.TrySetResult(Count);
                _enumerationCompletion = null;
            }
        }
    }

    public string GetDeviceName(byte deviceIdx)
    {
        lock (_devices)
        {
            if (_devices.TryGetValue(deviceIdx, out var device))
            {
                return device.DeviceName ?? $"HID++ Device {deviceIdx}";
            }
            else
            {
                return $"HID++ Device {deviceIdx}";
            }
        }
    }
    /// <summary>
    /// Disposes all devices in the collection and clears the collection.
    /// Called during HidppReceiver disposal to ensure proper cleanup.
    /// </summary>
    public void DisposeAll()
    {
        lock (_devices)
        {
            // Complete any pending enumeration with current count
            _enumerationCompletion?.TrySetResult(Count);
            _enumerationCompletion = null;
            _expectedDeviceCount = 0;

            foreach (var device in _devices.Values)
            {
                device.Dispose();
            }
            _devices.Clear();
            _lastInitTime.Clear(); // Clear init tracking
        }
    }
}
