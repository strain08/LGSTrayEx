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

        return reportId switch
        {
            0x50 => new CenturionTransportShort(dev),
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
}
