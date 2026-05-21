using System.Runtime.InteropServices;

namespace HidReportIdProbe;

[StructLayout(LayoutKind.Sequential)]
internal readonly unsafe struct HidDeviceInfo
{
    public readonly byte* Path;
    public readonly ushort VendorId;
    public readonly ushort ProductId;
    public readonly byte* SerialNumber;
    public readonly ushort ReleaseNumber;
    public readonly byte* ManufacturerString;
    public readonly byte* ProductString;
    public readonly ushort UsagePage;
    public readonly ushort Usage;
    public readonly int InterfaceNumber;
    public readonly HidDeviceInfo* Next;
    public readonly int BusType;
}

internal static partial class HidApi
{
    [LibraryImport("hidapi", EntryPoint = "hid_init")]
    public static partial int HidInit();

    [LibraryImport("hidapi", EntryPoint = "hid_exit")]
    public static partial int HidExit();

    [LibraryImport("hidapi", EntryPoint = "hid_enumerate")]
    public static unsafe partial HidDeviceInfo* HidEnumerate(ushort vendor_id, ushort product_id);

    [LibraryImport("hidapi", EntryPoint = "hid_free_enumeration")]
    public static unsafe partial void HidFreeEnumeration(HidDeviceInfo* devs);

    [LibraryImport("hidapi", EntryPoint = "hid_open_path")]
    public static unsafe partial nint HidOpenPath(byte* path);

    [LibraryImport("hidapi", EntryPoint = "hid_close")]
    public static partial void HidClose(nint dev);

    [LibraryImport("hidapi", EntryPoint = "hid_write")]
    public static partial int HidWrite(nint dev, [In] byte[] data, nuint length);

    public static unsafe string PathAsString(byte* path)
    {
        if (path == null) return "";
        return Marshal.PtrToStringAnsi((nint)path) ?? "";
    }

    public static unsafe string WidePathOrEmpty(byte* p)
    {
        if (p == null) return "";
        return Marshal.PtrToStringUni((nint)p) ?? "";
    }
}
