using LGSTrayCore;

namespace LGSTrayUI.Messages;

/// <summary>
/// Message sent by LogiDeviceCollection after updating device battery state.
/// Allows NotificationService to react AFTER device is guaranteed to exist in collection.
/// </summary>
public class DeviceBatteryUpdatedMessage(LogiDevice device)
{
    public LogiDevice Device => device;
}
