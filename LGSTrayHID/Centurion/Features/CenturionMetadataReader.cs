using LGSTrayHID.Centurion.Channel;
using LGSTrayHID.Metadata;
using LGSTrayPrimitives;

namespace LGSTrayHID.Centurion.Features;

/// <summary>
/// Reads device metadata (name, hardware info, serial) from a Centurion device via a send delegate.
/// Works for both direct mode and dongle (bridge) mode — the caller provides the appropriate
/// channel's SendAsync as the delegate.
/// </summary>
public sealed class CenturionMetadataReader(CenturionSendAsync sendAsync,
                                            byte deviceNameIdx,
                                            byte deviceInfoIdx)
{
    /// <summary>
    /// Read all available metadata. Returns the updated device name plus hardware identity fields.
    /// </summary>
    public async Task<(string deviceName, DeviceIdentity ids)> ReadAsync(string defaultDeviceName)
    {
        string name = await TryGetDeviceName() ?? defaultDeviceName;

        DeviceIdentity ids = await TryGetDeviceInfo();

        return (name, ids);
    }

    /// <summary>
    /// Reads the device name via the DeviceName feature.
    /// Supports both inline name response and chunked fallback.
    /// </summary>
    private async Task<string?> TryGetDeviceName()
    {
        if (deviceNameIdx == 0xFF) return null;
        try
        {
            // DeviceName func 0 = getNameLength, func 1 = getNameChunk
            var lengthResp = await sendAsync(deviceNameIdx, 0x00, [], 2000);

            if (lengthResp == null || lengthResp.Value.Params.Length == 0) return null;
            int nameLen = lengthResp.Value.Params[0];
            if (nameLen == 0 || nameLen > 64) return null;

            // --- INLINE NAME SUPPORT ---
            // Some Centurion devices return the full name in the first response if it fits.
            if (lengthResp.Value.Params.Length >= 1 + nameLen)
            {
                string name = System.Text.Encoding.UTF8.GetString(lengthResp.Value.Params, 1, nameLen).TrimEnd('\0');
                DiagnosticLogger.Log($"[Centurion] Device name (inline): {name}");
                return name;
            }

            // --- CHUNKED NAME FALLBACK ---
            // Otherwise, fetch name in chunks via function 1 (standard HID++ 2.0 behavior)
            var nameBytes = new List<byte>();
            for (int offset = 0; offset < nameLen; offset += 16)
            {
                var chunkResp = await sendAsync(deviceNameIdx, 0x01, [(byte)offset], 2000);

                if (chunkResp == null || chunkResp.Value.Params.Length == 0) break;
                nameBytes.AddRange(chunkResp.Value.Params);
            }
            if (nameBytes.Count > 0)
            {
                string name = System.Text.Encoding.UTF8
                    .GetString(nameBytes.ToArray(), 0, Math.Min(nameLen, nameBytes.Count))
                    .TrimEnd('\0');
                DiagnosticLogger.Log($"[Centurion] Device name (chunked): {name}");
                return name;
            }
        }
        catch (Exception ex) { DiagnosticLogger.LogWarning($"[Centurion] Device name read failed: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// Reads hardware info (model, revision) and serial number via DeviceInfo (0x0100).
    /// </summary>
    private async Task<DeviceIdentity> TryGetDeviceInfo()
    {
        if (deviceInfoIdx == 0xFF) return default;
        string? modelId = null, unitId = null, serialNumber = null;
        try
        {
            // func 0 = getHardwareInfo (modelId, hwRevision, productId)
            var hwResp = await sendAsync(deviceInfoIdx, 0x00, [], 2000);

            if (hwResp != null && hwResp.Value.Params.Length >= 4)
            {
                modelId = hwResp.Value.Params[0].ToString("X2");
                unitId = hwResp.Value.Params[1].ToString("X2"); // HW revision
                DiagnosticLogger.Log($"[Centurion] HW Info: Model={modelId}, Rev={unitId}");
            }

            // func 2 = getSerialNumber
            var snResp = await sendAsync(deviceInfoIdx, 0x02, [], 2000);

            if (snResp != null && snResp.Value.Params.Length >= 1)
            {
                int snLen = snResp.Value.Params[0];
                if (snLen > 0 && snResp.Value.Params.Length >= 1 + snLen)
                {
                    serialNumber = System.Text.Encoding.ASCII
                        .GetString(snResp.Value.Params, 1, snLen)
                        .TrimEnd('\0');
                    DiagnosticLogger.Log($"[Centurion] Serial Number: {serialNumber}");
                }
            }
        }
        catch (Exception ex) { DiagnosticLogger.LogWarning($"[Centurion] Device info read failed: {ex.Message}"); }
        return new DeviceIdentity(modelId, unitId, serialNumber);
    }
}
