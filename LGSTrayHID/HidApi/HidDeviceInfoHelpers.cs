using System.Runtime.InteropServices;

namespace LGSTrayHID.HidApi;

public enum HidppMessageType : short
{
    NONE = 0,
    SHORT,
    LONG,
    VERY_LONG,
    PROBE  // Non-FF00 vendor page with usage 0x0001 — Centurion headset candidate
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

    internal static HidppMessageType GetHidppMessageType(this HidDeviceInfo deviceInfo)
    {
        unsafe
        {
            // Standard HID++ usage page (FF00 only — not FF13, FFA0, etc.)
            if (deviceInfo.UsagePage == 0xFF00)
            {
                return deviceInfo.Usage switch
                {
                    0x0001 => HidppMessageType.SHORT,
                    0x0002 => HidppMessageType.LONG,
                    _ => HidppMessageType.NONE,
                };
            }

            // Other vendor-specific pages with usage 0x0001 — Centurion headset interfaces
            if ((deviceInfo.UsagePage & 0xFF00) == 0xFF00 && deviceInfo.Usage == 0x0001)
            {
                return HidppMessageType.PROBE;
            }

            return HidppMessageType.NONE;
        }
    }

}
