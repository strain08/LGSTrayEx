using LGSTrayUI.Services;

namespace LGSTrayUI.Messages;

/// <summary>
/// Message sent by LogiDeviceCollection after updating device battery state.
/// Allows NotificationService to react AFTER device is guaranteed to exist in collection.
/// </summary>
public class DeviceBatteryUpdatedMessage(LogiDeviceViewModel device)
{
    public LogiDeviceViewModel Device { get; } = device;
}
