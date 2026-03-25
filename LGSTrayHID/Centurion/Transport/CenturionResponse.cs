namespace LGSTrayHID.Centurion.Transport;

/// <summary>
/// Headset reachability as reported by a CentPPBridge ConnectionStateChanged frame.
/// </summary>
public enum HeadsetConnectionState
{
    /// <summary>Non-zero params — headset is connected and reachable.</summary>
    Online,
    /// <summary>All-zero params — headset is sleeping or absent.</summary>
    Offline,
    /// <summary>Empty params — malformed or truncated frame; state unknown.</summary>
    Unknown,
}

/// <summary>
/// Parsed Centurion CPL response frame.
/// </summary>
public readonly record struct CenturionResponse(byte FeatIdx, byte FuncId, byte SwId, byte[] Params)
{
    public HeadsetConnectionState ConnectionState => Params.Length == 0
        ? HeadsetConnectionState.Unknown
        : Params.All(b => b == 0)
            ? HeadsetConnectionState.Offline
            : HeadsetConnectionState.Online;
}
