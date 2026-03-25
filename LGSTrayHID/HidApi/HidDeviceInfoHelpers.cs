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
