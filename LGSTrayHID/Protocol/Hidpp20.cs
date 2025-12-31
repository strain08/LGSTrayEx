using System.Diagnostics.Metrics;

namespace LGSTrayHID.Protocol;

public readonly struct Hidpp20
{
    private readonly byte[] _data;

    public Hidpp20(byte[] data)
    {
        _data = data;
    }

    // conversion to/from byte array
    public static explicit operator byte[](Hidpp20 msg) => msg._data;
    public static implicit operator Hidpp20(byte[] data) => new(data);

    public byte this[int index] => _data[index];

    public int Length => _data.Length;

    public byte GetDeviceIdx() => _data[1];

    public byte GetFeatureIndex() => _data[2];

    public byte GetFunctionId() => (byte)((_data[3] & 0xF0) >> 4);

    public byte GetSoftwareId() => (byte)(_data[3] & 0x0F);

    public Span<byte> GetParams() => _data.AsSpan(4);

    public byte GetParam(int paramIdx) => _data[4 + paramIdx];

    // ========== Phase 2: Response Parsing Helpers ==========

    /// <summary>
    /// Check if this response is a HID++ error response.
    /// Error responses have feature index 0x8F.
    /// </summary>
    /// <returns>True if this is an error response</returns>
    public bool IsError() => GetFeatureIndex() == HidppResponse.ERROR;

    /// <summary>
    /// Check if this is a device announcement message (hotplug arrival).
    /// Device announcements have feature index 0x41.
    /// </summary>
    /// <returns>True if this is a device announcement</returns>
    public bool IsDeviceAnnouncement() => GetFeatureIndex() == HidppResponse.DEVICE_ANNOUNCEMENT;

    /// <summary>
    /// Check if this message is a battery event broadcast.
    /// Battery events have function index 0x00 and are NOT matched to a pending request.
    /// </summary>
    /// <param name="featureIndex">The feature index to check against (from device's feature map)</param>
    /// <returns>True if this is a battery event for the given feature</returns>
    /// <remarks>
    /// Battery events are distinguished from query responses by:
    /// 1. Function ID is 0x00 (BATTERY_STATUS_BROADCAST)
    /// 2. Not matched to a pending WriteRead20 request (caller's responsibility)
    /// 3. Feature index matches a known battery feature (0x1000, 0x1001, 0x1004)
    /// 
    /// Critical Requirements
    /// 1. Always Use Non-Zero Software ID
    ///  - Never use 0x00 in requests
    ///  - Recommended: Use 0x01 or incrementing counter(0x01-0x0F)
    ///  - Device events always have Software ID = 0x00
    /// </remarks>
    public bool IsBatteryEvent(byte featureIndex) =>
                    GetFeatureIndex() == featureIndex && 
                    GetFunctionId() == BatteryEventFunction.BATTERY_STATUS_BROADCAST && 
                    GetSoftwareId() == 0x00;

    /// <summary>
    /// Check if this is a DJ protocol notification (not HID++ message).
    /// DJ notifications have report IDs 0x20 or 0x21, while HID++ uses 0x10.
    /// </summary>
    /// <returns>True if this is a DJ notification</returns>
    /// <remarks>
    /// CRITICAL: Always check IsDJNotification() BEFORE checking IsDeviceAnnouncement()
    /// to avoid 0x41 disambiguation issues. DJ 0x41 (device paired) vs HID++ 0x41 (announcement).
    /// </remarks>
    public bool IsDJNotification() =>
               _data[0] == DJProtocol.REPORT_ID_SHORT ||
               _data[0] == DJProtocol.REPORT_ID_LONG;

    /// <summary>
    /// Get the DJ notification type (byte[2]) from a DJ notification.
    /// Only valid if IsDJNotification() returns true.
    /// </summary>
    /// <returns>DJ notification type (0x40, 0x41, 0x42)</returns>
    /// <remarks>
    /// Common types:
    /// - 0x40: Device unpaired
    /// - 0x41: Device paired
    /// - 0x42: Connection status change
    /// </remarks>
    public byte GetDJNotificationType() => _data[2];

    /// <summary>
    /// Get the error code from an error response.
    /// Only valid if IsError() returns true.
    /// </summary>
    /// <returns>Error code byte</returns>
    /// <exception cref="InvalidOperationException">Thrown if this is not an error response</exception>
    public byte GetErrorCode()
    {
        if (!IsError())
        {
            throw new InvalidOperationException("Cannot get error code from non-error response");
        }
        return GetParam(0);
    }

    /// <summary>
    /// Parse a 16-bit parameter from two consecutive bytes (big-endian).
    /// Commonly used for feature IDs and other multi-byte values.
    /// </summary>
    /// <param name="offset">Parameter offset (0-2)</param>
    /// <returns>16-bit unsigned value</returns>
    /// <example>
    /// // Instead of: ushort featureId = (ushort)((ret.GetParam(0) << 8) + ret.GetParam(1));
    /// // Use: ushort featureId = ret.GetParam16(0);
    /// </example>
    public ushort GetParam16(int offset) => (ushort)((GetParam(offset) << 8) | GetParam(offset + 1));

    /// <summary>
    /// Get the feature ID from a feature enumeration response.
    /// This is a convenience method that calls GetParam16(0).
    /// </summary>
    /// <returns>Feature ID as 16-bit unsigned value</returns>
    public ushort GetFeatureId() => GetParam16(0);

    /// <summary>
    /// Check if this response matches the given request.
    /// Matching is based on feature index and software ID.
    /// </summary>
    /// <param name="request">The original request command</param>
    /// <returns>True if the response matches the request</returns>
    public bool MatchesRequest(Hidpp20 request) => GetFeatureIndex() == request.GetFeatureIndex() &&
                                                   GetSoftwareId() == request.GetSoftwareId();

    public override string ToString()
    {
        return BitConverter.ToString(_data).Replace("-", " ");
    }
}
