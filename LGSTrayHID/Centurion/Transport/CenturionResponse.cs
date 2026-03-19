namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// Parsed Centurion CPL response frame.
/// </summary>
public readonly record struct CenturionResponse(byte FeatIdx, byte FuncId, byte SwId, byte[] Params)
{
    /// <summary>
    /// All-zero params means no sub-device connected (headset sleeping or absent)
    /// </summary>
    public bool HeadsetOffline => Params.Length > 0 && Params.All(b => b == 0);
    /// <summary>
    /// Not all params 0 means subdevice is present
    /// </summary>
    public bool HeadsetOnline => Params.Length > 0 && !Params.All(b => b == 0);
}
