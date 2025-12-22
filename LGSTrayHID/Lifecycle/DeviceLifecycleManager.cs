using System.Collections.Generic;

namespace LGSTrayHID.Lifecycle;

/// <summary>
/// Manages the lifecycle of HID++ devices, centralizing device collection operations.
/// Prevents direct dictionary mutation and provides a clean abstraction for device management.
/// </summary>
public class DeviceLifecycleManager
{
    private readonly Dictionary<ushort, HidppDevice> _devices = [];
    private readonly HidppReceiver _parent;

    public DeviceLifecycleManager(HidppReceiver parent)
    {
        _parent = parent;
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
    /// <returns>The newly created HidppDevice instance</returns>
    public HidppDevice CreateDevice(byte deviceIdx)
    {
        var device = new HidppDevice(_parent, deviceIdx);
        _devices[deviceIdx] = device;
        return device;
    }

    /// <summary>
    /// Attempts to retrieve a device by its index.
    /// </summary>
    /// <param name="deviceIdx">Device index to look up</param>
    /// <param name="device">The found device, or null if not found</param>
    /// <returns>True if device was found, false otherwise</returns>
    public bool TryGetDevice(byte deviceIdx, out HidppDevice? device)
    {
        return _devices.TryGetValue(deviceIdx, out device);
    }

    /// <summary>
    /// Gets the current number of devices in the collection.
    /// </summary>
    public int Count => _devices.Count;

    /// <summary>
    /// Disposes all devices in the collection and clears the collection.
    /// Called during HidppReceiver disposal to ensure proper cleanup.
    /// </summary>
    public void DisposeAll()
    {
        lock (_devices)
        {
            foreach (var device in _devices.Values)
            {
                device.Dispose();
            }
            _devices.Clear();
        }
    }
}
