using LGSTrayHID.Centurion.Transport;
using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID.Centurion;

/// <summary>
/// Builds the appropriate Centurion transport for a report ID already resolved from the HID
/// report descriptor (see <see cref="HidApi.HidppReportProbe"/>). The 0x50 variant additionally
/// requires a device-address probe, which is a device-I/O scan and cannot be read from the descriptor.
/// </summary>
public static class CenturionTransportFactory
{
    public static async Task<CenturionTransport> CreateAsync(HidDevicePtr dev, HidppReportId reportId, ushort productId, CancellationToken ct)
    {
        DiagnosticLogger.Log($"[Cent 0x{productId:X4}] Report ID 0x{(byte)reportId:X2} (from descriptor)");

        return reportId switch
        {
            HidppReportId.CenturionShort => new CenturionTransportShort(dev, await ProbeDeviceAddressAsync(dev, (byte)reportId, ct, productId), productId),
            HidppReportId.CenturionLong => new CenturionTransportLong(dev, productId),
            // Defensive: the descriptor classifier only routes 0x50/0x51 here. Any other value means
            // the report ID could not be resolved — fall back to passive sniffing (RX logging only).
            _ => Passive(dev, productId),
        };
    }

    private static CenturionTransport Passive(HidDevicePtr dev, ushort productId)
    {
        DiagnosticLogger.LogWarning($"[Cent 0x{productId:X4}] No Centurion report ID from descriptor — passive sniff mode (RX logging only)");
        return new CenturionTransportPassive(dev, productId);
    }

    /// <summary>
    /// Probe the device address for 0x50-variant devices.
    /// Modeled on Solaar's probe_centurion_device_addr (lib/logitech_receiver/base.py).
    /// Typical G522 (addr=0x23): ~180ms. Worst case scan: ~1.3s.
    /// </summary>
    /// <param name="dev"></param>
    /// <param name="productId">only for display purposes</param>
    /// <param name="reportId"></param>
    /// <param name="ct"></param>
    private static async Task<byte> ProbeDeviceAddressAsync(HidDevicePtr dev, byte reportId, CancellationToken ct, ushort productId = 0)
    {
        string tag = $"[0x{productId:X4}] Cent";
        byte[] buffer = new byte[FrameLayout.FRAME_SIZE];

        byte? scanned = await ProbeAddressActivelyAsync(dev, reportId, buffer, tag, ct);
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
    /// Active scan: 
    /// Send ROOT.GetProtocolVersion (feat=0x00, func=0x01) to every candidate device address 0x00–0xFF
    /// Modeled on Solaar's probe_centurion_device_addr (lib/logitech_receiver/base.py)
    /// ROOT.GetProtocolVersion: feat_idx=0x00, func=0x10, 3 zero ping params
    /// </summary>
    /// <returns>The address from the first reply, or null if no candidate responded</returns>
    private static async Task<byte?> ProbeAddressActivelyAsync(HidDevicePtr dev, byte reportId, byte[] readBuffer, string tag, CancellationToken ct)
    {
        // ROOT.GetProtocolVersion frame for Layout_0x50:
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
