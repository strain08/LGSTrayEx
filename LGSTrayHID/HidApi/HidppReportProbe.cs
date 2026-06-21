using System.Runtime.InteropServices;
using LGSTrayPrimitives;

namespace LGSTrayHID.HidApi;

/// <summary>
/// HID++/Centurion channel classifier. Reads the Windows HID report descriptor via HidP_*
/// and classifies the collection by its declared report IDs and report byte length:
///   report ID 0x10 + 7-byte input report             => SHORT (1 + 6 payload)
///   report ID 0x11 + 20-byte input report            => LONG  (1 + 19 payload)
///   report ID 0x51 in input AND output, 64-byte report => Centurion long  (PRO X 2)
///   report ID 0x50 in input AND output, 64-byte report => Centurion short (G522)
/// Source: Solaar (lib/hidapi/udev_impl.py) 
/// 0x51 is preferred over 0x50
/// Every failure path returns <see cref="HidppCollection.Unsupported"/>
/// </summary>
internal static class HidppReportProbe
{
    /// <summary>
    /// Parses raw descriptor evidence into a classified collection.
    /// Single-report collection: report byte length == 1 (report ID) + payload. HID++ is classified
    /// by input report ID + size; Centurion requires the report ID in BOTH input and output with a
    /// 64-byte report (1 id + 63 payload).
    /// </summary>
    private static HidppCollection Classify(HidDescriptor d)
    {
        // HID++ is classified by input report ID + size.
        if (d.InputReportIds.Contains(0x10) && 
            d.InputReportByteLength == 7)
            return new HidppCollection.Short();

        if (d.InputReportIds.Contains(0x11) && 
            d.InputReportByteLength == 20)
            return new HidppCollection.Long();

        // Centurion requires the report ID in BOTH input and output
        // with a 64-byte report (1 id + 63 payload)
        if (d.InputReportByteLength == 64 &&
            d.InputReportIds.Contains(0x51) &&
            d.OutputReportIds.Contains(0x51))
            return new HidppCollection.Centurion(CenturionReportId.Long);

        if (d.InputReportByteLength == 64 &&
            d.InputReportIds.Contains(0x50) &&
            d.OutputReportIds.Contains(0x50))
            return new HidppCollection.Centurion(CenturionReportId.Short);

        return new HidppCollection.Unsupported();
    }

    internal static HidppCollection ProbeFromDescriptor(string path)
    {
        try
        {
            HidDescriptor? descriptor = ReadDescriptor(path);
            if (descriptor is null) return new HidppCollection.Unsupported();

            HidppCollection result = Classify(descriptor) with { Descriptor = descriptor };

            // Dump the full declared report picture for every probed collection, match or not.            
            DiagnosticLogger.Log($"{Describe(result)} <- {descriptor} : {path}");

            return result;
        }
        catch (Exception ex)
        {
            // Guard: any P/Invoke / marshalling failure must not propagate out of InitDevice
            // (its queue loop is unguarded and an unobserved exception would stop device processing).
            DiagnosticLogger.LogError($"[Probe] Descriptor read failed for {path}: {ex.Message}");
            return new HidppCollection.Unsupported();
        }
    }
    

    /// <summary>
    /// The core read: opens the device metadata-only and reads its declared input/output report IDs
    /// and sizes into a <see cref="HidDescriptor"/>. Returns null (logging the reason) if the
    /// descriptor cannot be read. No device I/O.
    /// </summary>
    private static HidDescriptor? ReadDescriptor(string path)
    {
        using var handle = HidP.CreateFile(
            path,
            0, // metadata only - no read/write access, non-exclusive
            HidP.FILE_SHARE_READ | HidP.FILE_SHARE_WRITE,
            0,
            HidP.OPEN_EXISTING,
            0,
            0);

        if (handle.IsInvalid)
        {
            DiagnosticLogger.LogError($"{path}: CreateFile failed (Win32 {Marshal.GetLastWin32Error()}) - descriptor unreadable");
            return null;
        }

        if (!HidP.HidD_GetPreparsedData(handle, out nint preparsed) || preparsed == 0)
        {
            DiagnosticLogger.LogError($"{path}: HidD_GetPreparsedData returned null - descriptor unreadable");
            return null;
        }

        try
        {
            int capsStatus = HidP.HidP_GetCaps(preparsed, out HIDP_CAPS caps);
            if (capsStatus != HidP.HIDP_STATUS_SUCCESS)
            {
                DiagnosticLogger.LogError($"{path}: HidP_GetCaps failed (HIDP_STATUS 0x{capsStatus:X8}) - descriptor unreadable");
                return null;
            }

            var inputIds = new HashSet<byte>();
            var outputIds = new HashSet<byte>();
            CollectReportIds(HIDP_REPORT_TYPE.HidP_Input, preparsed, caps, inputIds, path);
            CollectReportIds(HIDP_REPORT_TYPE.HidP_Output, preparsed, caps, outputIds, path);

            return new HidDescriptor(
                caps.UsagePage, caps.Usage,
                inputIds, caps.InputReportByteLength,
                outputIds, caps.OutputReportByteLength);
        }
        finally
        {
            HidP.HidD_FreePreparsedData(preparsed);
        }
    }

    private static string Describe(HidppCollection collection) => collection switch
    {
        HidppCollection.Centurion c => $"Centurion (0x{c.ReportIdByte:X2})",
        _ => collection.GetType().Name,
    };

    private static void CollectReportIds(HIDP_REPORT_TYPE reportType, nint preparsed, in HIDP_CAPS caps, HashSet<byte> sink, string path)
    {
        bool isInput = reportType == HIDP_REPORT_TYPE.HidP_Input;
        ushort valueCount = isInput ? caps.NumberInputValueCaps : caps.NumberOutputValueCaps;
        ushort buttonCount = isInput ? caps.NumberInputButtonCaps : caps.NumberOutputButtonCaps;

        if (valueCount > 0)
        {
            var arr = new HIDP_VALUE_CAPS[valueCount];
            ushort len = valueCount;
            int status = HidP.HidP_GetValueCaps(reportType, arr, ref len, preparsed);
            if (status == HidP.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++) sink.Add(arr[i].ReportID);
            }
            else
            {
                DiagnosticLogger.LogError($"{path}: HidP_GetValueCaps ({reportType}) failed (HIDP_STATUS 0x{status:X8})");
            }
        }
        if (buttonCount > 0)
        {
            var arr = new HIDP_BUTTON_CAPS[buttonCount];
            ushort len = buttonCount;
            int status = HidP.HidP_GetButtonCaps(reportType, arr, ref len, preparsed);
            if (status == HidP.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++) sink.Add(arr[i].ReportID);
            }
            else
            {
                DiagnosticLogger.LogError($"{path}: HidP_GetButtonCaps ({reportType}) failed (HIDP_STATUS 0x{status:X8})");
            }
        }
    }
}
