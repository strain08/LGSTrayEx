using LGSTrayHID.HidApi;

namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// 0x50 (G522, Centurion SHORT) transport variant.
/// RX frames have an extra device-address byte at [1]; TX is still symmetric.
/// </summary>
public sealed class CenturionTransportShort(HidDevicePtr dev)
    : CenturionTransport(dev, reportId: 0x50)
{
    protected override FrameLayout RxLayout => FrameLayout.Layout_0x50;
    // TxLayout intentionally NOT overridden — TX is symmetric for 0x50
}
