using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HidReportIdProbe;

// Windows HID descriptor APIs. References:
//   https://learn.microsoft.com/en-us/windows/win32/api/hidsdi/nf-hidsdi-hidd_getpreparseddata
//   https://learn.microsoft.com/en-us/windows/win32/api/hidpi/nf-hidpi-hidp_getcaps
//   https://learn.microsoft.com/en-us/windows/win32/api/hidpi/nf-hidpi-hidp_getvaluecaps
//   https://learn.microsoft.com/en-us/windows/win32/api/hidpi/nf-hidpi-hidp_getbuttoncaps

[StructLayout(LayoutKind.Sequential)]
internal struct HIDP_CAPS
{
    public ushort Usage;
    public ushort UsagePage;
    public ushort InputReportByteLength;
    public ushort OutputReportByteLength;
    public ushort FeatureReportByteLength;

    public ushort Reserved0;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public ushort Reserved4;
    public ushort Reserved5;
    public ushort Reserved6;
    public ushort Reserved7;
    public ushort Reserved8;
    public ushort Reserved9;
    public ushort Reserved10;
    public ushort Reserved11;
    public ushort Reserved12;
    public ushort Reserved13;
    public ushort Reserved14;
    public ushort Reserved15;
    public ushort Reserved16;

    public ushort NumberLinkCollectionNodes;
    public ushort NumberInputButtonCaps;
    public ushort NumberInputValueCaps;
    public ushort NumberInputDataIndices;
    public ushort NumberOutputButtonCaps;
    public ushort NumberOutputValueCaps;
    public ushort NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps;
    public ushort NumberFeatureValueCaps;
    public ushort NumberFeatureDataIndices;
}

// Shared layout between HIDP_VALUE_CAPS and HIDP_BUTTON_CAPS — both expose ReportID + ReportCount + BitSize.
// We only inspect ReportID and (for value caps) BitSize × ReportCount to estimate per-report size.
[StructLayout(LayoutKind.Sequential)]
internal struct HIDP_VALUE_CAPS
{
    public ushort UsagePage;
    public byte ReportID;
    public byte IsAlias;
    public ushort BitField;
    public ushort LinkCollection;
    public ushort LinkUsage;
    public ushort LinkUsagePage;
    public byte IsRange;
    public byte IsStringRange;
    public byte IsDesignatorRange;
    public byte IsAbsolute;
    public byte HasNull;
    public byte Reserved;
    public ushort BitSize;
    public ushort ReportCount;
    public ushort Reserved2_0;
    public ushort Reserved2_1;
    public ushort Reserved2_2;
    public ushort Reserved2_3;
    public ushort Reserved2_4;
    public uint UnitsExp;
    public uint Units;
    public int LogicalMin;
    public int LogicalMax;
    public int PhysicalMin;
    public int PhysicalMax;
    // Union starts here (16 bytes either way)
    public ushort UsageMin;
    public ushort UsageMax;
    public ushort StringMin;
    public ushort StringMax;
    public ushort DesignatorMin;
    public ushort DesignatorMax;
    public ushort DataIndexMin;
    public ushort DataIndexMax;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HIDP_BUTTON_CAPS
{
    public ushort UsagePage;
    public byte ReportID;
    public byte IsAlias;
    public ushort BitField;
    public ushort LinkCollection;
    public ushort LinkUsage;
    public ushort LinkUsagePage;
    public byte IsRange;
    public byte IsStringRange;
    public byte IsDesignatorRange;
    public byte IsAbsolute;
    public byte Reserved0;
    public ushort Reserved1_0;
    public ushort Reserved1_1;
    public ushort Reserved1_2;
    public ushort Reserved1_3;
    public ushort Reserved1_4;
    public ushort Reserved1_5;
    public ushort Reserved1_6;
    public ushort Reserved1_7;
    public ushort Reserved1_8;
    public ushort Reserved1_9;
    // Union — 16 bytes
    public ushort UsageMin;
    public ushort UsageMax;
    public ushort StringMin;
    public ushort StringMax;
    public ushort DesignatorMin;
    public ushort DesignatorMax;
    public ushort DataIndexMin;
    public ushort DataIndexMax;
}

internal enum HIDP_REPORT_TYPE : int
{
    HidP_Input = 0,
    HidP_Output = 1,
    HidP_Feature = 2,
}

internal static partial class HidP
{
    public const uint GENERIC_READ  = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ  = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("hid.dll", EntryPoint = "HidD_GetPreparsedData")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out nint preparsedData);

    [LibraryImport("hid.dll", EntryPoint = "HidD_FreePreparsedData")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_FreePreparsedData(nint preparsedData);

    // HidP_GetCaps returns HIDP_STATUS_SUCCESS (0x00110000) on success.
    public const int HIDP_STATUS_SUCCESS = unchecked((int)0x00110000);

    [LibraryImport("hid.dll", EntryPoint = "HidP_GetCaps")]
    public static partial int HidP_GetCaps(nint preparsedData, out HIDP_CAPS caps);

    [LibraryImport("hid.dll", EntryPoint = "HidP_GetValueCaps")]
    public static partial int HidP_GetValueCaps(
        HIDP_REPORT_TYPE reportType,
        [Out] HIDP_VALUE_CAPS[] valueCaps,
        ref ushort valueCapsLength,
        nint preparsedData);

    [LibraryImport("hid.dll", EntryPoint = "HidP_GetButtonCaps")]
    public static partial int HidP_GetButtonCaps(
        HIDP_REPORT_TYPE reportType,
        [Out] HIDP_BUTTON_CAPS[] buttonCaps,
        ref ushort buttonCapsLength,
        nint preparsedData);
}
