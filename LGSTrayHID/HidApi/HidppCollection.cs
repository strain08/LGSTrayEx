namespace LGSTrayHID.HidApi;

/// <summary>
/// The Centurion transport report ID, resolved from the report descriptor.
/// </summary>
public enum CenturionReportId : byte
{
    Short = 0x50,  // G522 — addressed variant (device_addr byte at frame[1])
    Long  = 0x51,  // PRO X 2 — symmetric variant
}

/// <summary>
/// Raw framing evidence read from a HID report descriptor
/// </summary>
public sealed record HidDescriptor(
    ushort UsagePage,
    ushort Usage,
    IReadOnlySet<byte> InputReportIds,
    int InputReportByteLength,
    IReadOnlySet<byte> OutputReportIds,
    int OutputReportByteLength)
{
    public override string ToString() =>
        $"page 0x{UsagePage:X04} usage 0x{Usage:X04} " +
        $"in {{{Format(InputReportIds)}}} inLen={InputReportByteLength} " +
        $"out {{{Format(OutputReportIds)}}} outLen={OutputReportByteLength}";

    private static string Format(IReadOnlySet<byte> ids) =>
        ids.Count == 0 ? "none" : string.Join(", ", ids.OrderBy(b => b).Select(b => $"0x{b:X2}"));
}

/// <summary>
/// Classification of a single HID collection. <br/>
/// The set of variants is closed to the ones declared here <br/>
/// Exhausted with pattern matching and a throwing discard.
/// </summary>
public abstract record HidppCollection
{
    /// <summary>
    /// Logitech webcams can lock up during HID++ probing <br/>
    /// PID range from solaar docs/usb.ids.txt
    /// </summary>
    /// <param name="productId"></param>
    /// <returns></returns>
    private static bool IsExcludedFromProbe(ushort productId) => 
        (productId is >= 0x0800 and <= 0x09FF); // Webcams
    
    private protected HidppCollection() { }

    /// <summary>Descriptor evidence, or null when classified by usage page alone.</summary>
    public HidDescriptor? Descriptor { get; init; }

    /// <summary>
    /// Classifies a collection by report descriptor first (report ID + size is protocol-intrinsic)    
    /// </summary>
    internal static HidppCollection Classify(HidDeviceInfo deviceInfo)
    {
        if (IsExcludedFromProbe(deviceInfo.ProductId)) return new Unsupported();
        
        return HidppReportProbe.ProbeFromDescriptor(deviceInfo.GetPath());        
    }

    /// <summary>HID++ short channel: report 0x10, 7-byte frames.</summary>
    public sealed record Short : HidppCollection;

    /// <summary>HID++ long channel: report 0x11, 20-byte frames.</summary>
    public sealed record Long : HidppCollection;

    /// <summary>Centurion transport (PRO X 2 / G522): report 0x50/0x51, 64-byte frames.</summary>
    public sealed record Centurion(CenturionReportId ReportId) : HidppCollection
    {
        public byte ReportIdByte => (byte)ReportId;
    }
    
    /// <summary>Collection we don't support or can't classify — skip it.</summary>
    public sealed record Unsupported : HidppCollection;
}
