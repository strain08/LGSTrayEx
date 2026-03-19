using LGSTrayHID.HidApi;

namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// Passive/sniff-only transport — RunReadLoopAsync works, SendRequest throws.
/// Used when the report ID cannot be detected (unknown Centurion variant).
/// </summary>
public sealed class CenturionTransportPassive(HidDevicePtr dev)
    : CenturionTransport(dev, reportId: 0x00)
{
    public override bool IsPassive => true;
}
