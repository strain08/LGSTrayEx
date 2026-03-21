using LGSTrayHID.HidApi;

namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// 0x50 (G522, Centurion SHORT) transport variant.
/// Both TX and RX frames include a device address byte (0x23) at position [1].
/// </summary>
public sealed class CenturionTransportShort(HidDevicePtr dev)
    : CenturionTransport(dev, reportId: 0x50)
{
    protected override FrameLayout TxLayout => FrameLayout.Layout_0x50;
    protected override FrameLayout RxLayout => FrameLayout.Layout_0x50;
}
