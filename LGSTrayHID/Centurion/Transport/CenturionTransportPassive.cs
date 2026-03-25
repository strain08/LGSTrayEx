using LGSTrayHID.HidApi;

namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// Passive/sniff-only transport — RunReadLoopAsync works, SendRequest throws.
/// Used when the report ID cannot be detected (unknown Centurion variant).
/// </summary>
public sealed class CenturionTransportPassive(HidDevicePtr dev, ushort productId = 0)
    : CenturionTransport(dev, reportId: 0x00, productId)
{
    public override bool IsPassive => true;
}
