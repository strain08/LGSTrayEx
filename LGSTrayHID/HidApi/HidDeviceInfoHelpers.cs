using System.Runtime.InteropServices;

namespace LGSTrayHID.HidApi;

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

}
