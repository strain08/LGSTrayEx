using LGSTrayHID.Centurion.Transport;

namespace LGSTrayHID.Centurion.Channel;

/// <summary>
/// Direct (non-bridge) Centurion channel for USB-connected devices.
/// </summary>
public sealed class CenturionDirectChannel(CenturionTransport transport, CancellationToken ct)
    : CenturionChannel(transport, ct)
{
    public override Task<CenturionResponse?> SendAsync(byte featIdx,
                                                       byte func,
                                                       byte[] parameters,
                                                       int timeoutMs = 2000)
        => SendCoreAsync(featIdx, func, timeoutMs,() => 
            Transport.SendDirectRequest(featIdx, func, parameters));

    public override bool TryCompleteRequest(CenturionResponse frame) => TrySetPending(frame);
}
