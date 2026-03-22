using LGSTrayHID.HidApi;

namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// 0x50 (G522, Centurion SHORT) transport variant.
/// Both TX and RX frames include a device address byte at position [1].
/// The address is probed from the device's first RX frame during factory creation.
/// </summary>
public sealed class CenturionTransportShort : CenturionTransport
{
    private readonly FrameLayout _layout;

    public CenturionTransportShort(HidDevicePtr dev, byte deviceAddress)
        : base(dev, reportId: 0x50)
    {
        _layout = FrameLayout.Layout_0x50(deviceAddress);
    }

    protected override FrameLayout TxLayout => _layout;
    protected override FrameLayout RxLayout => _layout;
}
