using LGSTrayHID.Centurion.Transport;
using LGSTrayPrimitives;

namespace LGSTrayHID.Centurion.Channel;

/// <summary>
/// Bridge (dongle) Centurion channel — wraps requests in CentPPBridge envelopes
/// and unwraps MessageEvent responses.
/// </summary>
public class CenturionBridgeChannel : CenturionChannel
{
    private readonly byte _bridgeIdx;
    private readonly byte _subDeviceId;

    public CenturionBridgeChannel(CenturionTransport transport,
                                  byte bridgeIdx,
                                  byte subDeviceId,
                                  CancellationToken ct)
        : base(transport, ct)
    {
        _bridgeIdx = bridgeIdx;
        _subDeviceId = subDeviceId;
    }
    
    public override Task<CenturionResponse?> SendAsync(byte featIdx,
                                                       byte func,
                                                       byte[] parameters,
                                                       int timeoutMs = 2000)
        => SendCoreAsync(featIdx, 
                         func, 
                         timeoutMs + 1000,
                         () => Transport.SendBridgeRequest(_bridgeIdx,
                                                            _subDeviceId,
                                                            featIdx,
                                                            func,
                                                            parameters)
                        );

    /// <summary>
    /// Handle SWID frames in bridge mode.
    /// Bridge sendMessage ACK (FuncId==0x01) is swallowed — the real headset response arrives
    /// as a MessageEvent (SwId==0) handled by TryHandleEvent.
    /// </summary>
    public override bool TryCompleteRequest(CenturionResponse frame)
    {
        if (frame.FeatIdx == _bridgeIdx && frame.FuncId == 0x01)
        {
            DiagnosticLogger.Verbose("[Centurion] Bridge: sendMessage ACK (ignored, awaiting MessageEvent)");
            return true;
        }
        return TrySetPending(frame);
    }

    /// <summary>
    /// Unwrap bridge MessageEvent frames (SwId==0, FeatIdx==_bridgeIdx, FuncId==0x01).
    /// </summary>
    public override CenturionResponse? RouteEvent(CenturionResponse frame)
    {
        if (frame.FeatIdx != _bridgeIdx || frame.FuncId != 0x01)
            return frame; // not a bridge frame — pass through

        var subFrame = Transport.UnwrapBridgedFrame(frame.Params);
        if (subFrame == null)
            return null; // consumed but malformed

        if (subFrame.Value.SwId == CenturionTransport.SWID)
        {
            // Solicited response: complete the pending bridge request
            TrySetPending(subFrame.Value);
            return null; // consumed
        }

        // Unsolicited sub-device event — forward to HandleAsyncEvent
        return subFrame;
    }
}
