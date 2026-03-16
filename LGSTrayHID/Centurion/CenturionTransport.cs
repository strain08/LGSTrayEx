using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID.Centurion;

/// <summary>
/// Parsed Centurion CPL response frame.
/// </summary>
public readonly record struct CenturionResponse(byte FeatIdx, byte FuncId, byte SwId, byte[] Params);

/// <summary>
/// Low-level Centurion CPL frame I/O on a single HID device handle.
/// Handles both direct and bridge-wrapped (dongle) communication.
///
/// CPL frame format (64 bytes, zero-padded):
///   [reportId] [cpl_length] [flags] [feat_idx] [func&lt;&lt;4 | swid] [params...]
/// </summary>
public class CenturionTransport : IDisposable
{
    private readonly HidDevicePtr _dev;
    private readonly byte _reportId;

    private const int FRAME_SIZE = 64;
    private const byte FLAGS_SINGLE = 0x00;
    private const byte SWID = 0x0A; // Match GlobalSettings default

    public CenturionTransport(HidDevicePtr dev, byte reportId = 0x51)
    {
        _dev = dev;
        _reportId = reportId;
    }

    /// <summary>
    /// Build and send a direct CPL request frame.
    /// </summary>
    public async Task SendRequest(byte featIdx, byte func, byte[] parameters)
    {
        byte[] frame = BuildFrame(featIdx, func, parameters);
        LogTx("Direct", featIdx, func, parameters);
        await _dev.WriteAsync(frame);
    }

    /// <summary>
    /// Build and send a CentPPBridge-wrapped request for sub-device communication (dongle mode).
    ///
    /// Bridge envelope format:
    ///   [reportId] [cpl_length] [flags=0x00] [bridge_idx] [0x10|swid]
    ///   [dev_id&lt;&lt;4 | len_hi] [len_lo]
    ///   [sub_cpl=0x00] [sub_feat_idx] [sub_func|swid] [params...]
    /// </summary>
    public async Task SendBridgeRequest(byte bridgeIdx, byte devId, byte subFeatIdx, byte subFunc, byte[] subParams)
    {
        // Sub-device message: [sub_cpl=0x00] [sub_feat_idx] [sub_func|swid] [params...]
        int subMsgLen = 3 + subParams.Length;

        // Bridge header params: [devId<<4 | lenHi] [lenLo] [sub-message...]
        byte[] bridgeParams = new byte[2 + subMsgLen];
        bridgeParams[0] = (byte)((devId << 4) | ((subMsgLen >> 8) & 0x0F));
        bridgeParams[1] = (byte)(subMsgLen & 0xFF);
        bridgeParams[2] = FLAGS_SINGLE; // sub-device CPL flags
        bridgeParams[3] = subFeatIdx;
        bridgeParams[4] = (byte)((subFunc << 4) | SWID);
        Array.Copy(subParams, 0, bridgeParams, 5, subParams.Length);

        // Bridge outer frame: func=0x01 (sendMessage)
        byte[] frame = BuildFrame(bridgeIdx, 0x01, bridgeParams);

        DiagnosticLogger.Verbose($"[Centurion] TX Bridge: bridgeIdx={bridgeIdx} devId={devId} " +
                                 $"subFeat=0x{subFeatIdx:X2} subFunc={subFunc} " +
                                 $"payload={BitConverter.ToString(frame, 0, Math.Min(frame.Length, 20))}...");

        await _dev.WriteAsync(frame);
    }

    /// <summary>
    /// Read a single CPL response frame with timeout.
    /// Returns null on timeout or disconnect.
    /// </summary>
    public CenturionResponse? ReadResponse(int timeoutMs = 2000)
    {
        byte[] buffer = new byte[FRAME_SIZE];
        int bytesRead = _dev.Read(buffer, FRAME_SIZE, timeoutMs);

        if (bytesRead <= 0)
            return null;

        LogRx(buffer, bytesRead);
        return ParseFrame(buffer, bytesRead);
    }

    /// <summary>
    /// Read a bridge response: discard the immediate ACK, then read the async MessageEvent
    /// and unwrap the bridge envelope to return the sub-device payload.
    /// </summary>
    public CenturionResponse? ReadBridgeResponse(byte bridgeIdx, int timeoutMs = 3000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            int remainingMs = Math.Max(50, (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds);
            var response = ReadResponse(remainingMs);

            if (response == null)
                return null;

            var r = response.Value;

            // Skip ACK frames (func=0x01 sendMessage ACK, swid matches ours)
            if (r.FeatIdx == bridgeIdx && r.FuncId == 0x01 && r.SwId == SWID)
            {
                DiagnosticLogger.Verbose("[Centurion] Bridge ACK received, waiting for MessageEvent...");
                continue;
            }

            // MessageEvent: bridge feature, func=0x01, swid=0x00 (async event)
            // Or func=0x00 with swid=0x00 for some firmware versions
            if (r.FeatIdx == bridgeIdx && r.SwId == 0x00 && r.Params.Length >= 5)
            {
                return UnwrapBridgePayload(r.Params);
            }

            // Not a bridge response - could be unsolicited event, skip
            DiagnosticLogger.Verbose($"[Centurion] Skipping non-bridge frame: feat=0x{r.FeatIdx:X2} func={r.FuncId} swid={r.SwId}");
        }

        DiagnosticLogger.LogWarning("[Centurion] Bridge response timeout");
        return null;
    }

    /// <summary>
    /// Continuously read frames, invoking the callback for each one.
    /// Runs until cancellation or disconnect.
    /// </summary>
    public void ReadLoop(Action<CenturionResponse> onFrame, CancellationToken ct)
    {
        byte[] buffer = new byte[FRAME_SIZE];

        while (!ct.IsCancellationRequested)
        {
            int bytesRead = _dev.Read(buffer, FRAME_SIZE, 500);

            if (bytesRead < 0)
            {
                DiagnosticLogger.Log("[Centurion] Device disconnected (read loop)");
                break;
            }

            if (bytesRead > 0)
            {
                LogRx(buffer, bytesRead);
                var parsed = ParseFrame(buffer, bytesRead);
                if (parsed != null)
                    onFrame(parsed.Value);
            }
        }
    }

    public void Close()
    {
        HidClose(_dev);
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    // ---- Private helpers ----

    private byte[] BuildFrame(byte featIdx, byte func, byte[] parameters)
    {
        byte[] frame = new byte[FRAME_SIZE];
        frame[0] = _reportId;
        frame[1] = (byte)(3 + parameters.Length); // cpl_length = flags + featIdx + func|swid + params
        frame[2] = FLAGS_SINGLE;
        frame[3] = featIdx;
        frame[4] = (byte)((func << 4) | SWID);
        Array.Copy(parameters, 0, frame, 5, parameters.Length);
        return frame;
    }

    private static CenturionResponse? ParseFrame(byte[] buffer, int bytesRead)
    {
        // Minimum valid frame: reportId + cplLen + flags + featIdx + func|swid = 5 bytes
        if (bytesRead < 5)
            return null;

        byte cplLen = buffer[1];
        byte featIdx = buffer[3];
        byte funcSwid = buffer[4];
        byte funcId = (byte)((funcSwid >> 4) & 0x0F);
        byte swId = (byte)(funcSwid & 0x0F);

        // Params start at byte 5, length is cplLen - 3 (flags + featIdx + func|swid)
        int paramLen = Math.Max(0, cplLen - 3);
        int available = Math.Max(0, bytesRead - 5);
        paramLen = Math.Min(paramLen, available);

        byte[] parameters = new byte[paramLen];
        if (paramLen > 0)
            Array.Copy(buffer, 5, parameters, 0, paramLen);

        return new CenturionResponse(featIdx, funcId, swId, parameters);
    }

    private static CenturionResponse? UnwrapBridgePayload(byte[] bridgeParams)
    {
        // Bridge payload: [devId<<4|lenHi] [lenLo] [sub_flags] [sub_featIdx] [sub_func|swid] [params...]
        if (bridgeParams.Length < 5)
            return null;

        int subMsgLen = ((bridgeParams[0] & 0x0F) << 8) | bridgeParams[1];
        if (bridgeParams.Length < 2 + subMsgLen || subMsgLen < 3)
            return null;

        byte subFeatIdx = bridgeParams[3];
        byte subFuncSwid = bridgeParams[4];
        byte subFuncId = (byte)((subFuncSwid >> 4) & 0x0F);
        byte subSwId = (byte)(subFuncSwid & 0x0F);

        int subParamLen = subMsgLen - 3;
        byte[] subParams = new byte[subParamLen];
        if (subParamLen > 0)
            Array.Copy(bridgeParams, 5, subParams, 0, Math.Min(subParamLen, bridgeParams.Length - 5));

        return new CenturionResponse(subFeatIdx, subFuncId, subSwId, subParams);
    }

    private static void LogTx(string mode, byte featIdx, byte func, byte[] parameters)
    {
        DiagnosticLogger.Verbose($"[Centurion] TX {mode}: feat=0x{featIdx:X2} func={func} " +
                                 $"params={BitConverter.ToString(parameters)}");
    }

    private static void LogRx(byte[] buffer, int bytesRead)
    {
        DiagnosticLogger.Verbose($"[Centurion] RX ({bytesRead} bytes): {BitConverter.ToString(buffer, 0, bytesRead)}");
    }
}
