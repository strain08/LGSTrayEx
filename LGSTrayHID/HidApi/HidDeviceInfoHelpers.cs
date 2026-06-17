using System.Runtime.InteropServices;

namespace LGSTrayHID.HidApi;

public enum HidppMessageType : short
{
    NONE = 0,
    SHORT,
    LONG,
    VERY_LONG,
    CENTURION  // Non-FF00 vendor page with usage 0x0001 — Centurion headset candidate
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

            // Centurion transport: wireless Lightspeed dongles (PRO X 2, G522 dongle)
            (0xFFA0, 0x0001) => HidppMessageType.CENTURION,

            _ => HidppMessageType.NONE,
        };
    }

}
