using LGSTrayHID.HidApi;

namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// 0x50 (G522, Centurion SHORT) transport variant.
/// Both TX and RX frames include a device address byte at position [1].
/// The address is probed from the device's first RX frame during factory creation.
/// </summary>
public sealed class CenturionTransportShort : CenturionTransport
{
    private readonly byte _deviceAddress;

    public CenturionTransportShort(HidDevicePtr dev, byte deviceAddress, ushort productId = 0)
        : base(dev, reportId: 0x50, productId)
    {
        _deviceAddress = deviceAddress;
    }

    protected override FrameLayout TxLayout => FrameLayout.Layout_0x50;
    protected override FrameLayout RxLayout => FrameLayout.Layout_0x50;

    protected override byte[] BuildFrame(byte featIdx, byte func, byte[] parameters)
    {
        byte[] frame = CenturionTransport.BuildFrame(TxLayout, ReportId, featIdx, func, parameters);
        frame[1] = _deviceAddress;
        return frame;
    }
}
