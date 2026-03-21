using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using System.Threading.Channels;
using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// Low-level Centurion CPL frame I/O on a single HID device handle.
/// Handles both direct and bridge-wrapped (dongle) communication.
///
/// CPL frame format (64 bytes, zero-padded):
///   [reportId] [cpl_length] [flags] [feat_idx] [func<<4 | swid] [params...]
/// </summary>
public class CenturionTransport : IDisposable
{
    private readonly HidDevicePtr _dev;
    protected readonly byte ReportId;

    /// <summary>True when the report ID could not be detected; the transport can read but not send.</summary>
    public virtual bool IsPassive => false;

    private const byte FLAGS_SINGLE = 0x00;
    public const byte SWID = 0x0A; // Match GlobalSettings default

    /// <summary>TX frame layout. Override in subclasses if the TX format differs from Layout_0x51.</summary>
    protected virtual FrameLayout TxLayout => FrameLayout.Layout_0x51;

    /// <summary>RX frame layout. Override in subclasses whose RX frames differ from the symmetric 0x51 format.</summary>
    protected virtual FrameLayout RxLayout => FrameLayout.Layout_0x51;

    protected CenturionTransport(HidDevicePtr dev, byte reportId)
    {
        _dev = dev;
        ReportId = reportId;
    }

    /// <summary>
    /// Build and send a direct CPL request frame.
    /// </summary>
    public virtual async Task SendDirectRequest(byte featIdx, byte func, byte[] parameters)
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
    public virtual async Task SendBridgeRequest(byte bridgeIdx, byte devId, byte subFeatIdx, byte subFunc, byte[] subParams)
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
    public virtual async Task RunReadLoopAsync(ChannelWriter<CenturionResponse> writer, CancellationToken ct)
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
                    
                    var parsed = ParseFrame(RxLayout, buffer, bytesRead);
                    if (parsed != null && !writer.TryWrite(parsed.Value))
                        await writer.WriteAsync(parsed.Value, ct);
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
    /// </summary>   
    public virtual CenturionResponse? UnwrapBridgedFrame(byte[] bridgeParams)
        => UnwrapBridgePayload(bridgeParams);

    /// <summary>
    /// Pure algorithm — testable without a device instance.
    /// </summary>
    public static CenturionResponse? UnwrapBridgePayload(byte[] bridgeParams)
    {
        //                 0                1       2           3             4               5+  
        // Bridge payload: [devId<<4|lenHi] [lenLo] [sub_flags] [sub_featIdx] [sub_func|swid] [params...]
        if (bridgeParams.Length < 5)
        {
            DiagnosticLogger.Verbose($"[Centurion] UnwrapBridge: too short ({bridgeParams.Length} bytes, need 5)");
            return null;
        }

        // total 12 bit max subMessage length = 4095 bytes
        int subMsgLen = ((bridgeParams[0] & 0x0F) << 8) | bridgeParams[1];
        // sub-message needs at least [flags][featIdx][func|swid]
        // subMsgLen < 3 rejects malformed frames
        if (bridgeParams.Length < 2 + subMsgLen || subMsgLen < 3)
        {
            DiagnosticLogger.Verbose($"[Centurion] UnwrapBridge: malformed subMsgLen={subMsgLen} available={bridgeParams.Length - 2} raw={BitConverter.ToString(bridgeParams)}");
            return null;
        }

        byte subFeatIdx = bridgeParams[3];
        byte subFuncSwid = bridgeParams[4];
        byte subFuncId = (byte)((subFuncSwid >> 4) & 0x0F);
        byte subSwId   = (byte)(subFuncSwid & 0x0F);
        
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
        if (IsPassive)
            throw new InvalidOperationException("Cannot send: transport is in passive sniff mode");
        return BuildFrame(TxLayout, ReportId, featIdx, func, parameters);
    }
    //private CenturionResponse? ParseFrame(byte[] buffer, int bytesRead) => ParseFrame(RxLayout, buffer, bytesRead);

    // ---- Internal static overloads (testable without a HID device) ----

    /// <summary>
    /// Build a 64-byte CPL request frame.
    /// For Layout_0x50: includes device address (0x23) at [1], matching the RX format.
    /// For Layout_0x51: symmetric (no device address).
    /// </summary>
    internal static byte[] BuildFrame(FrameLayout layout, byte reportId, byte featIdx, byte func, byte[] parameters)
    {
        byte[] frame = new byte[FrameLayout.FRAME_SIZE];
        frame[0] = reportId;
        if (layout.DeviceAddress.HasValue)
            frame[1] = layout.DeviceAddress.Value;
        frame[layout.CplLenOffset]   = (byte)(3 + parameters.Length);
        frame[layout.FlagsOffset]    = FLAGS_SINGLE;
        frame[layout.FeatIdxOffset]  = featIdx;
        frame[layout.FuncSwidOffset] = (byte)((func << 4) | SWID);
        Array.Copy(parameters, 0, frame, layout.ParamsOffset, parameters.Length);
        return frame;
    }

    /// <summary>
    /// Parse a received CPL frame. Returns null if too short to hold a valid header.
    /// RX layout differs by variant: Layout_0x50 (G522) has an extra device-address byte
    /// (0x23) at [1] that shifts all field offsets by one vs Layout_0x51 (PRO X 2).
    /// </summary>
    internal static CenturionResponse? ParseFrame(FrameLayout layout, byte[] buffer, int bytesRead)
    {
        // CPL RX frame — Layout_0x51 (PRO X 2, symmetric):         Layout_0x50 (G522, indexed):
        //   [0]  reportId (0x51)                                      [0]  reportId (0x50)
        //                                                             [1]  0x23 (device address, RX only)
        //   [CplLenOffset]   cplLen  (flags+featIdx+func|swid+params) [CplLenOffset]   cplLen
        //   [FlagsOffset]    0x00                                     [FlagsOffset]    0x00
        //   [FeatIdxOffset]  featIdx                                  [FeatIdxOffset]  featIdx
        //   [FuncSwidOffset] func<<4 | swId                           [FuncSwidOffset] func<<4 | swId
        //   [ParamsOffset..] params[0..n]                             [ParamsOffset..] params[0..n]
        //
        // swId==0x00 → unsolicited device event
        // swId==SWID → response to our request
        // swId==other → foreign software frame, ignored by dispatcher
        if (bytesRead < 3) return null;

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
