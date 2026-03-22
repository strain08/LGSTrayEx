using LGSTrayHID.Centurion.Transport;
using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using System.Diagnostics;
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

    public static async Task<CenturionTransport> CreateAsync(HidDevicePtr dev, ushort productId, CancellationToken ct)
    {
        byte? reportId = ProbeReportId(dev);
        if (reportId.HasValue)
            DiagnosticLogger.Log($"[Cent 0x{productId:X4}] Detected variant: report ID 0x{reportId:X2}");
        else
            DiagnosticLogger.LogWarning($"[Cent 0x{productId:X4}] Unknown report ID — passive sniff mode (RX logging only)");

        return reportId switch
        {
            0x50 => new CenturionTransportShort(dev, await ProbeDeviceAddressAsync(dev, reportId.Value, ct, productId), productId),
            0x51 => new CenturionTransportLong(dev, productId),
            null => new CenturionTransportPassive(dev, productId),
            // All defined candidates must be assigned to transports
            _ => throw new UnreachableException($"ProbeReportId returned 0x{reportId:X2}: defined transport not assigned")
        };
    }

    private static byte? ProbeReportId(HidDevicePtr dev)
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
    /// Loops indefinitely until a valid frame arrives or cancellation is requested —
    /// without the address, TX frames cannot be addressed correctly.
    /// </summary>
    private static async Task<byte> ProbeDeviceAddressAsync(HidDevicePtr dev, byte reportId, CancellationToken ct, ushort productId = 0)
    {
        string tag = $"[0x{productId:X4}] Cent";
        byte[] buffer = new byte[FrameLayout.FRAME_SIZE];
        DiagnosticLogger.Log($"{tag} Waiting for DEVICE_ADDRESS frame...");

        while (!ct.IsCancellationRequested)
        {
            // 2000ms read timeout so we can check cancellation between attempts
            int bytesRead = await Task.Run(() => dev.Read(buffer, FrameLayout.FRAME_SIZE, 2000), ct);

            if (bytesRead < 0)
            {
                DiagnosticLogger.LogError($"{tag} Device error while probing DEVICE_ADDRESS — aborting");
                break;
            }

            if (bytesRead >= 2 && buffer[0] == reportId)
            {
                byte addr = buffer[1];
                DiagnosticLogger.Log($"{tag} Probed DEVICE_ADDRESS: 0x{addr:X2}");
                return addr;
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException($"{tag} Could not probe DEVICE_ADDRESS: device error");
    }
}
