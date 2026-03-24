namespace LGSTrayHID.Metadata;

/// <summary>
/// Hardware identity fields retrieved from device firmware (HID++ Feature 0x0003 / Centurion DeviceInfo).
/// All fields are optional — availability depends on device firmware capabilities.
/// </summary>
public readonly record struct DeviceIdentity(string? ModelId, string? UnitId, string? SerialNumber);
