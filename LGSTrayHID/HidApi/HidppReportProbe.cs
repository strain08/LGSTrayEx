using System.Runtime.InteropServices;
using LGSTrayPrimitives;

namespace LGSTrayHID.HidApi;

/// <summary>
/// HID++/Centurion channel classifier. Reads the Windows HID report descriptor via HidP_*
/// (no device I/O, no blind writes) and classifies the collection by its declared report IDs
/// and report byte length:
///   report ID 0x10 + 7-byte input report             => SHORT (1 + 6 payload)
///   report ID 0x11 + 20-byte input report            => LONG  (1 + 19 payload)
///   report ID 0x51 in input AND output, 64-byte report => Centurion long  (PRO X 2)
///   report ID 0x50 in input AND output, 64-byte report => Centurion short (G522)
/// The HID++ size-based rules and the Centurion input+output rule both mirror Solaar
/// (lib/hidapi/udev_impl.py); 0x51 is preferred over 0x50, matching Solaar. Every failure path
/// returns <see cref="HidppProbeResult.None"/>, so the caller degrades to the pre-existing
/// "skip unsupported device" behaviour.
/// </summary>
internal static class HidppReportProbe
{
    internal static HidppProbeResult ProbeFromDescriptor(string path)
    {
        try
        {
            return ProbeFromDescriptorCore(path);
        }
        catch (Exception ex)
        {
            // Guard: any P/Invoke / marshalling failure must not propagate out of InitDevice
            // (its queue loop is unguarded and an unobserved exception would stop device processing).
            DiagnosticLogger.Log($"[Probe] Descriptor read failed for {path}: {ex.Message}");
            return HidppProbeResult.None;
        }
    }

    private static HidppProbeResult ProbeFromDescriptorCore(string path)
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
            DiagnosticLogger.Log($"[Probe] {path}: CreateFile failed (Win32 {Marshal.GetLastWin32Error()}) - descriptor unreadable");
            return HidppProbeResult.None;
        }

        if (!HidP.HidD_GetPreparsedData(handle, out nint preparsed) || preparsed == 0)
        {
            DiagnosticLogger.Log($"[Probe] {path}: HidD_GetPreparsedData returned null - descriptor unreadable");
            return HidppProbeResult.None;
        }

        try
        {
            if (HidP.HidP_GetCaps(preparsed, out HIDP_CAPS caps) != HidP.HIDP_STATUS_SUCCESS)
            {
                DiagnosticLogger.Log($"[Probe] {path}: HidP_GetCaps failed - descriptor unreadable");
                return HidppProbeResult.None;
            }

            var inputIds = new HashSet<byte>();
            var outputIds = new HashSet<byte>();
            CollectReportIds(HIDP_REPORT_TYPE.HidP_Input, preparsed, caps, inputIds);
            CollectReportIds(HIDP_REPORT_TYPE.HidP_Output, preparsed, caps, outputIds);

            // Single-report collection: report byte length == 1 (report ID) + payload.
            // HID++ is classified by input report ID + size; Centurion requires the report ID in
            // BOTH input and output with a 64-byte report (1 id + 63 payload), mirroring Solaar.
            HidppReportId reportId =
                (inputIds.Contains(0x10) && caps.InputReportByteLength == 7) ? HidppReportId.Short :
                (inputIds.Contains(0x11) && caps.InputReportByteLength == 20) ? HidppReportId.Long :
                (caps.InputReportByteLength == 64 && inputIds.Contains(0x51) && outputIds.Contains(0x51)) ? HidppReportId.CenturionLong :
                (caps.InputReportByteLength == 64 && inputIds.Contains(0x50) && outputIds.Contains(0x50)) ? HidppReportId.CenturionShort :
                HidppReportId.None;

            var result = new HidppProbeResult(reportId);

            // Dump the full declared report picture for every probed collection, match or not.
            // This is the diagnostic the daemon log otherwise lacks for skipped (NONE) collections -
            // e.g. it reveals a G733 long channel on a non-FF00 page, or a Centurion 0x50/0x51 collection.
            DiagnosticLogger.Log(
                $"[Probe] {path}: page 0x{caps.UsagePage:X04} usage 0x{caps.Usage:X04} " +
                $"in {{{FormatIds(inputIds)}}} inLen={caps.InputReportByteLength} " +
                $"out {{{FormatIds(outputIds)}}} outLen={caps.OutputReportByteLength} " +
                $"-> {result.MessageType} (reportId 0x{(byte)reportId:X2})");

            return result;
        }
        finally
        {
            HidP.HidD_FreePreparsedData(preparsed);
        }
    }

    private static string FormatIds(HashSet<byte> ids) =>
        ids.Count == 0 ? "none" : string.Join(", ", ids.OrderBy(b => b).Select(b => $"0x{b:X2}"));

    private static void CollectReportIds(HIDP_REPORT_TYPE reportType, nint preparsed, in HIDP_CAPS caps, HashSet<byte> sink)
    {
        bool isInput = reportType == HIDP_REPORT_TYPE.HidP_Input;
        ushort valueCount = isInput ? caps.NumberInputValueCaps : caps.NumberOutputValueCaps;
        ushort buttonCount = isInput ? caps.NumberInputButtonCaps : caps.NumberOutputButtonCaps;

        if (valueCount > 0)
        {
            var arr = new HIDP_VALUE_CAPS[valueCount];
            ushort len = valueCount;
            if (HidP.HidP_GetValueCaps(reportType, arr, ref len, preparsed) == HidP.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++) sink.Add(arr[i].ReportID);
            }
        }
        if (buttonCount > 0)
        {
            var arr = new HIDP_BUTTON_CAPS[buttonCount];
            ushort len = buttonCount;
            if (HidP.HidP_GetButtonCaps(reportType, arr, ref len, preparsed) == HidP.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++) sink.Add(arr[i].ReportID);
            }
        }
    }
}
