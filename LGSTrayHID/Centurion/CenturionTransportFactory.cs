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
    /// Probe the device address for 0x50-variant devices.
    /// First attempts an active scan: sends a well-formed ROOT.GetProtocolVersion request
    /// to every candidate address 0x00–0xFF and reads briefly after each write. The dongle
    /// silently ignores wrong addresses and replies only to the correct one. Falls back to
    /// passive wait if the active scan returns nothing (e.g. headset asleep behind the dongle).
    ///
    /// Modeled on Solaar's probe_centurion_device_addr (lib/logitech_receiver/base.py).
    /// Typical G522 (addr=0x23): ~180ms. Worst case scan: ~1.3s.
    /// </summary>
    private static async Task<byte> ProbeDeviceAddressAsync(HidDevicePtr dev, byte reportId, CancellationToken ct, ushort productId = 0)
    {
        string tag = $"[0x{productId:X4}] Cent";
        byte[] buffer = new byte[FrameLayout.FRAME_SIZE];

        byte? scanned = await ProbeAddressActivelyAsync(dev, reportId, buffer, ct, tag);
        if (scanned.HasValue)
            return scanned.Value;

        DiagnosticLogger.Log($"{tag} Active probe found nothing; falling back to passive wait for DEVICE_ADDRESS frame");

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
                DiagnosticLogger.Log($"{tag} Probed DEVICE_ADDRESS: 0x{addr:X2} (passive)");
                return addr;
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException($"{tag} Could not probe DEVICE_ADDRESS: device error");
    }

    /// <summary>
    /// Active scan: send ROOT.GetProtocolVersion (feat=0x00, func=0x01) to every candidate
    /// device address 0x00–0xFF, reading briefly after each write. Returns the address from
    /// the first reply, or null if no candidate responded.
    /// </summary>
    private static async Task<byte?> ProbeAddressActivelyAsync(HidDevicePtr dev, byte reportId, byte[] readBuffer, CancellationToken ct, string tag)
    {
        // Well-formed ROOT.GetProtocolVersion frame for Layout_0x50:
        //   [0]    reportId       (0x50)
        //   [1]    device addr    (varied 0x00..0xFF)
        //   [2]    cpl_length     (= 6: flags + featIdx + func|swid + 3 ping params)
        //   [3]    flags          (0x00)
        //   [4]    feat_idx       (0x00 = ROOT)
        //   [5]    func<<4|swid   ((0x01 << 4) | SWID)
        //   [6..8] ping params    (0x00 0x00 0x00)
        //   [9..]  padding        (0x00)
        byte[] frame = new byte[FrameLayout.FRAME_SIZE];
        frame[0] = reportId;
        frame[2] = 6;
        frame[3] = 0x00;
        frame[4] = 0x00;
        frame[5] = (byte)((0x01 << 4) | CenturionTransport.SWID);

        DiagnosticLogger.Log($"{tag} Active probe: scanning 256 candidate device addresses");

        const int perAddressTimeoutMs = 5;
        int writeFailures = 0;

        for (int addr = 0; addr < 256; addr++)
        {
            ct.ThrowIfCancellationRequested();
            frame[1] = (byte)addr;

            if (HidWrite(dev, frame, FrameLayout.FRAME_SIZE) <= 0)
            {
                if (++writeFailures > 3)
                {
                    DiagnosticLogger.LogWarning($"{tag} Active probe: too many write failures, aborting scan");
                    return null;
                }
                continue;
            }

            int n = await Task.Run(() => dev.Read(readBuffer, FrameLayout.FRAME_SIZE, perAddressTimeoutMs), ct);
            if (n < 0)
            {
                DiagnosticLogger.LogWarning($"{tag} Active probe: device error at addr 0x{addr:X2}");
                return null;
            }
            if (n >= 2 && readBuffer[0] == reportId)
            {
                byte found = readBuffer[1];
                DiagnosticLogger.Log($"{tag} Probed DEVICE_ADDRESS: 0x{found:X2} (active scan, {addr + 1} candidate(s) tried)");
                return found;
            }
        }

        DiagnosticLogger.Log($"{tag} Active probe: no response from any of 256 candidates");
        return null;
    }
}
