using System.Runtime.InteropServices;
using LGSTrayPrimitives;

namespace LGSTrayHID.HidApi;

/// <summary>
/// Fallback HID++ channel detector for devices whose usage page <see cref="HidDeviceInfoHelpers.GetHidppMessageType"/>
/// does not recognise (e.g. G733-class headsets that expose their channel on a non-FF00/FF43 page).
/// Reads the Windows HID report descriptor via HidP_* (no device I/O, no blind writes) and classifies
/// the collection by its declared input report ID and report byte length:
///   report ID 0x10 + 7-byte input report  => SHORT (1 + 6 payload)
///   report ID 0x11 + 20-byte input report => LONG  (1 + 19 payload)
/// This mirrors Solaar's size-based detection. Every failure path returns <see cref="HidppMessageType.NONE"/>,
/// so the caller degrades to the pre-existing "skip unsupported device" behaviour.
/// </summary>
internal static class HidppReportProbe
{
    internal static HidppMessageType ProbeFromDescriptor(string path)
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
            return HidppMessageType.NONE;
        }
    }

    private static HidppMessageType ProbeFromDescriptorCore(string path)
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
            return HidppMessageType.NONE;
        }

        if (!HidP.HidD_GetPreparsedData(handle, out nint preparsed) || preparsed == 0)
        {
            DiagnosticLogger.Log($"[Probe] {path}: HidD_GetPreparsedData returned null - descriptor unreadable");
            return HidppMessageType.NONE;
        }

        try
        {
            if (HidP.HidP_GetCaps(preparsed, out HIDP_CAPS caps) != HidP.HIDP_STATUS_SUCCESS)
            {
                DiagnosticLogger.Log($"[Probe] {path}: HidP_GetCaps failed - descriptor unreadable");
                return HidppMessageType.NONE;
            }

            var inputIds = new HashSet<byte>();
            CollectInputReportIds(preparsed, caps, inputIds);

            // Single-report HID++ collection: InputReportByteLength == 1 (report ID) + payload.
            HidppMessageType result =
                (inputIds.Contains(0x10) && caps.InputReportByteLength == 7) ? HidppMessageType.SHORT :
                (inputIds.Contains(0x11) && caps.InputReportByteLength == 20) ? HidppMessageType.LONG :
                HidppMessageType.NONE;

            // Dump the full declared input-report picture for every probed collection, match or not.
            // This is the diagnostic the daemon log otherwise lacks for skipped (NONE) collections -
            // e.g. it reveals a G733 long channel sitting on a non-FF00 page under report 0x11/0x12.
            DiagnosticLogger.Log(
                $"[Probe] {path}: page 0x{caps.UsagePage:X04} usage 0x{caps.Usage:X04} " +
                $"input report IDs {{{FormatIds(inputIds)}}} inLen={caps.InputReportByteLength} -> {result}");

            return result;
        }
        finally
        {
            HidP.HidD_FreePreparsedData(preparsed);
        }
    }

    private static string FormatIds(HashSet<byte> ids) =>
        ids.Count == 0 ? "none" : string.Join(", ", ids.OrderBy(b => b).Select(b => $"0x{b:X2}"));

    private static void CollectInputReportIds(nint preparsed, in HIDP_CAPS caps, HashSet<byte> sink)
    {
        if (caps.NumberInputValueCaps > 0)
        {
            var arr = new HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
            ushort len = caps.NumberInputValueCaps;
            if (HidP.HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, arr, ref len, preparsed) == HidP.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++) sink.Add(arr[i].ReportID);
            }
        }
        if (caps.NumberInputButtonCaps > 0)
        {
            var arr = new HIDP_BUTTON_CAPS[caps.NumberInputButtonCaps];
            ushort len = caps.NumberInputButtonCaps;
            if (HidP.HidP_GetButtonCaps(HIDP_REPORT_TYPE.HidP_Input, arr, ref len, preparsed) == HidP.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++) sink.Add(arr[i].ReportID);
            }
        }
    }
}
