using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using System.Threading.Channels;
using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID.Centurion;

/// <summary>
/// Parsed Centurion CPL response frame.
/// </summary>
public readonly record struct CenturionResponse(byte FeatIdx, byte FuncId, byte SwId, byte[] Params)
{
    /// <summary>
    /// All-zero params means no sub-device connected (headset sleeping or absent)
    /// </summary>
    public bool HeadsetOffline => Params.Length > 0 && Params.All(b => b == 0);
    /// <summary>
    /// Not All-zero params means subdevice is present
    /// </summary>
    public bool HeadsetOnline => Params.Length > 0 && !Params.All(b => b == 0);
}

/// <summary>
/// Low-level Centurion CPL frame I/O on a single HID device handle.
/// Handles both direct and bridge-wrapped (dongle) communication.
///
/// CPL frame format (64 bytes, zero-padded):
///   [reportId] [cpl_length] [flags] [feat_idx] [func<<4 | swid] [params...]
/// </summary>
public partial class CenturionTransport : IDisposable
{
    private readonly HidDevicePtr _dev;
    private readonly byte? _reportId; // null = passive (sniff-only) mode — unknown report ID

    /// <summary>True when the report ID could not be detected; the transport can read but not send.</summary>
    public bool IsPassive => !_reportId.HasValue;
    private const byte FLAGS_SINGLE = 0x00;
    public const byte SWID = 0x0A; // Match GlobalSettings default

    public CenturionTransport(HidDevicePtr dev)
    {
        _dev = dev;
        _reportId = FrameLayout.DetectReportId(dev);
        if (_reportId.HasValue)
            DiagnosticLogger.Log($"[Centurion] Detected variant: report ID 0x{_reportId:X2}");
        else
            DiagnosticLogger.LogWarning("[Centurion] Unknown report ID — passive sniff mode (RX logging only)");
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
    ///   [dev_id<<4 | len_hi] [len_lo]
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
    /// Continuous read loop that pumps frames into a System.Threading.Channel.
    /// This is the sole consumer of the HID handle's read buffer.
    /// </summary>
    public async Task RunReadLoopAsync(ChannelWriter<CenturionResponse> writer, CancellationToken ct)
    {
        DiagnosticLogger.Log("[Centurion] Transport read loop started");
        byte[] buffer = new byte[FrameLayout.FRAME_SIZE];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Task.Run yields to prevent CPU spinning if read is fast but dispatcher is busy
                int bytesRead = await Task.Run(() => _dev.Read(buffer, FrameLayout.FRAME_SIZE, 500), ct);

                if (bytesRead < 0)
                {
                    DiagnosticLogger.LogWarning("[Centurion] Device disconnected or read error");
                    break;
                }

                if (bytesRead > 0)
                {
                    LogRx(buffer, bytesRead);
                    var parsed = ParseFrame(buffer, bytesRead);
                    if (parsed != null)
                    {
                        if (!writer.TryWrite(parsed.Value))
                        {
                            await writer.WriteAsync(parsed.Value, ct);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[Centurion] Transport read loop exception: {ex.Message}");
        }
        finally
        {
            writer.TryComplete();
            DiagnosticLogger.Log("[Centurion] Transport read loop ended");
        }
    }

    /// <summary>
    /// Unwrap a bridge payload from a MessageEvent parameter buffer.
    /// Static so it can be used by the Dispatcher in CenturionDevice.
    /// </summary>
    public static CenturionResponse? UnwrapBridgePayload(byte[] bridgeParams)
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
        if (!_reportId.HasValue)
            throw new InvalidOperationException("Cannot send: transport is in passive sniff mode");
        byte[] frame = new byte[FrameLayout.FRAME_SIZE];
        frame[0] = _reportId.Value;
        frame[1] = (byte)(3 + parameters.Length); // cpl_length = flags + featIdx + func|swid + params
        frame[2] = FLAGS_SINGLE;
        frame[3] = featIdx;
        frame[4] = (byte)((func << 4) | SWID);
        Array.Copy(parameters, 0, frame, 5, parameters.Length);
        return frame;
    }
    
    private static CenturionResponse? ParseFrame(byte[] buffer, int bytesRead)
    {
        if (bytesRead < 3) return null;

        var layout = FrameLayout.GetLayout(buffer[0]);

        // Need at least all header bytes up to and including func|swid
        if (bytesRead <= layout.FuncSwidOffset) return null;

        byte cplLen   = buffer[layout.CplLenOffset];
        byte featIdx  = buffer[layout.FeatIdxOffset];
        byte funcSwid = buffer[layout.FuncSwidOffset];
        byte funcId   = (byte)((funcSwid >> 4) & 0x0F);
        byte swId     = (byte)(funcSwid & 0x0F);

        // Params length = cplLen - 3 (flags + featIdx + func|swid)
        int paramLen  = Math.Max(0, cplLen - 3);
        int available = Math.Max(0, bytesRead - layout.ParamsOffset);
        paramLen      = Math.Min(paramLen, available);

        byte[] parameters = new byte[paramLen];
        if (paramLen > 0)
            Array.Copy(buffer, layout.ParamsOffset, parameters, 0, paramLen);

        return new CenturionResponse(featIdx, funcId, swId, parameters);
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