using LGSTrayHID.HidApi;
using static LGSTrayHID.HidApi.HidApi;
namespace LGSTrayHID.Centurion;

// RX frame layout: byte offsets vary by report ID.
//
// 0x51 (PRO X 2, Centurion LONG) — symmetric, no device address:
//   [0] reportId  [1] cplLen  [2] flags  [3] featIdx  [4] func|swid  [5+] params
//
// 0x50 (G522, Centurion SHORT) — device address 0x23 inserted at [1] in every RX frame:
//   [0] reportId  [1] 0x23(devAddr)  [2] cplLen  [3] flags  [4] featIdx  [5] func|swid  [6+] params
//
// TX frames are always symmetric (no device address) regardless of report ID.
public readonly record struct FrameLayout(
    int CplLenOffset,   // byte index of payloadLen
    int FeatIdxOffset,  // byte index of feature index
    int FuncSwidOffset, // byte index of func<<4|swid
    int ParamsOffset)    // byte index of first param byte
{
    public static FrameLayout Layout_0x51 => new(CplLenOffset: 1, FeatIdxOffset: 3, FuncSwidOffset: 4, ParamsOffset: 5);
    public static FrameLayout Layout_0x50 => new(CplLenOffset: 2, FeatIdxOffset: 4, FuncSwidOffset: 5, ParamsOffset: 6);
    public static FrameLayout GetLayout(byte reportId) => reportId switch
    {
        0x50 => Layout_0x50,
        _ => Layout_0x51,   // 0x51 and any future symmetric variants
    };
    
    // 0x51 = PRO X 2 / Centurion LONG (most common, symmetric RX)
    // 0x50 = G522 / Centurion SHORT (RX has extra device-address byte)
    private static readonly byte[] ReportIdCandidates = [0x51, 0x50];
    public const int FRAME_SIZE = 64;

    /// <summary>
    /// Candidate report IDs tried in order.
    ///  hid_write fails with -1 immediately on wrong report id
    /// </summary>    
    /// <returns>
    /// report id or null
    /// </returns>
    public static byte? DetectReportId(HidDevicePtr dev)
    {
        byte[] probe = new byte[FRAME_SIZE];
        foreach (byte reportId in ReportIdCandidates)
        {
            probe[0] = reportId;
            if (HidWrite(dev, probe, FRAME_SIZE) > 0)
                return reportId;
        }
        return null;
    }
}
