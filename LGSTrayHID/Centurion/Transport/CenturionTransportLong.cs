using LGSTrayHID.HidApi;

namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// 0x51 (PRO X 2, Centurion LONG) transport variant — symmetric TX/RX, no device address.
/// Also used as the fallback for any unknown report ID that uses the same symmetric frame format.
/// </summary>
public sealed class CenturionTransportLong(HidDevicePtr dev, ushort productId = 0, byte reportId = 0x51)
    : CenturionTransport(dev, reportId, productId)
{
}
