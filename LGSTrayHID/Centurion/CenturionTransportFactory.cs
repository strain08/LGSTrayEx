using LGSTrayHID.Centurion.Transport;
using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID.Centurion;

/// <summary>
/// Determines the appropriate Centurion transport implementation 
/// by probing the connected HID device for supported report IDs.
/// </summary>
public static class CenturionTransportFactory
{
    // Candidate report IDs tried in order    
    private static readonly byte[] ReportIdCandidates = [0x51, 0x50];

    public static CenturionTransport Create(HidDevicePtr dev)
    {
        byte? reportId = DetectReportId(dev);
        if (reportId.HasValue)
            DiagnosticLogger.Log($"[Centurion] Detected variant: report ID 0x{reportId:X2}");
        else
            DiagnosticLogger.LogWarning("[Centurion] Unknown report ID — passive sniff mode (RX logging only)");

        if (reportId == 0x50)
        {
            byte deviceAddr = ProbeDeviceAddress(dev, reportId.Value);
            return new CenturionTransportShort(dev, deviceAddr);
        }

        return reportId switch
        {
            0x51 => new CenturionTransportLong(dev),
            null => new CenturionTransportPassive(dev),
            _    => new CenturionTransportLong(dev, reportId.Value) // unknown variant, try base layout
        };
    }

    private static byte? DetectReportId(HidDevicePtr dev)
    {
        byte[] probe = new byte[FrameLayout.FRAME_SIZE];
        foreach (byte reportId in ReportIdCandidates)
        {
            probe[0] = reportId;
            // hid_write returns -1 immediately on incompatible report ID
            if (HidWrite(dev, probe, FrameLayout.FRAME_SIZE) > 0)
                return reportId;
        }
        return null;
    }

    /// <summary>
    /// Probe the device address for 0x50-variant devices by reading the first RX frame.
    /// The address is always at byte[1] of every frame the device sends.
    /// Falls back to 0x23 (G522 default) if no frame arrives within the timeout.
    /// </summary>
    private static byte ProbeDeviceAddress(HidDevicePtr dev, byte reportId)
    {
        const byte FALLBACK_ADDR = 0x23;

        // The DetectReportId write (all-zeros with valid report ID) may have triggered
        // a response or error frame. Try reading it. Also catches any pending unsolicited frame.
        byte[] buffer = new byte[FrameLayout.FRAME_SIZE];
        int bytesRead = dev.Read(buffer, FrameLayout.FRAME_SIZE, 500);

        if (bytesRead >= 2 && buffer[0] == reportId)
        {
            byte addr = buffer[1];
            DiagnosticLogger.Log($"[Centurion] Probed device address: 0x{addr:X2}");
            return addr;
        }

        DiagnosticLogger.LogWarning($"[Centurion] Could not probe device address, using fallback 0x{FALLBACK_ADDR:X2}");
        return FALLBACK_ADDR;
    }
}
