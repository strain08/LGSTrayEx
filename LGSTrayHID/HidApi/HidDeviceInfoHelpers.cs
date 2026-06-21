using System.Runtime.InteropServices;

namespace LGSTrayHID.HidApi;

public enum HidppMessageType : short
{
    NONE = 0,
    SHORT,
    LONG,
    VERY_LONG,
    CENTURION  // Centurion transport (PRO X 2 / G522); resolved from the report descriptor by HidppReportProbe
}

/// <summary>
/// The HID++/Centurion input+output report ID declared by a collection's report descriptor.
/// This is the primitive fact read from the descriptor; <see cref="HidppProbeResult.MessageType"/>
/// is the interpretation. Values are the raw report-ID bytes.
/// </summary>
public enum HidppReportId : byte
{
    None           = 0x00,
    Short          = 0x10,  // HID++ short  (7-byte report:  1 id + 6 payload)
    Long           = 0x11,  // HID++ long   (20-byte report: 1 id + 19 payload)
    CenturionShort = 0x50,  // Centurion addressed variant (G522): device_addr byte at frame[1]
    CenturionLong  = 0x51,  // Centurion symmetric variant (PRO X 2)
}

/// <summary>
/// Result of <see cref="HidppReportProbe.ProbeFromDescriptor"/>. The report ID is the source of
/// truth; the message type is derived from it so the CENTURION→{0x50,0x51} fan-out is expressed once.
/// </summary>
public readonly record struct HidppProbeResult(HidppReportId ReportId)
{
    public HidppMessageType MessageType => ReportId switch
    {
        HidppReportId.Short => HidppMessageType.SHORT,
        HidppReportId.Long => HidppMessageType.LONG,
        HidppReportId.CenturionShort or HidppReportId.CenturionLong => HidppMessageType.CENTURION,
        _ => HidppMessageType.NONE,
    };

    public static readonly HidppProbeResult None = new(HidppReportId.None);
}

internal static class HidDeviceInfoHelpers
{
    internal static string GetPath(this HidDeviceInfo deviceInfo)
    {
        unsafe
        {
            return Marshal.PtrToStringAnsi((nint)deviceInfo.Path)!;
        }
    }

    internal static string? GetProductString(this HidDeviceInfo deviceInfo)
    {
        unsafe
        {
            if (deviceInfo.ProductString == null) return null;
            return Marshal.PtrToStringUni((nint)deviceInfo.ProductString);
        }
    }

    internal static HidppMessageType GetHidppMessageType(this HidDeviceInfo deviceInfo)
    {
        // Logitech vendor HID++ channel page 0xFF43 (e.g. G733 headset, Litra).
        // Unlike 0xFF00, the usage packs two bytes: the high byte is a capability
        // bitmask (bit0 short / bit1 long / bit2 very-long) and the low byte marks
        // which report size THIS collection defines (0x01 short, 0x02 long, 0x04
        // very-long). So e.g. 0x0202 and 0x0702 are both LONG channels.
        if (deviceInfo.UsagePage == 0xFF43)
        {
            return (deviceInfo.Usage & 0x00FF) switch
            {
                0x01 => HidppMessageType.SHORT,
                0x02 => HidppMessageType.LONG,
                0x04 => HidppMessageType.VERY_LONG,
                _ => HidppMessageType.NONE,
            };
        }

        return (deviceInfo.UsagePage, deviceInfo.Usage) switch
        {
            // Standard HID++
            (0xFF00, 0x0001) => HidppMessageType.SHORT,
            (0xFF00, 0x0002) => HidppMessageType.LONG,

            // Centurion transport (usage page 0xFFA0) is no longer classified here: it is resolved
            // from the report descriptor by HidppReportProbe so the specific report ID (0x50/0x51)
            // is known up front. Such collections return NONE here and fall through to the probe.
            _ => HidppMessageType.NONE,
        };
    }

}
