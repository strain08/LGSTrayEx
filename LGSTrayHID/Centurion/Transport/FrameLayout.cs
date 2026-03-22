namespace LGSTrayHID.Centurion.Transport;

// RX frame layout: byte offsets vary by report ID.
//
// 0x51 (PRO X 2, Centurion LONG) — symmetric, no device address:
//   [0] reportId  [1] cplLen  [2] flags  [3] featIdx  [4] func|swid  [5+] params
//
// 0x50 (G522, Centurion SHORT) — device address inserted at [1] in every frame:
//   [0] reportId  [1] devAddr  [2] cplLen  [3] flags  [4] featIdx  [5] func|swid  [6+] params
//
// The device address is a protocol detail of CenturionTransportShort; it is not part of the layout.
public readonly record struct FrameLayout(
    int CplLenOffset,   // byte index of payloadLen
    int FlagsOffset,    // byte index of flags
    int FeatIdxOffset,  // byte index of feature index
    int FuncSwidOffset, // byte index of func<<4|swid
    int ParamsOffset)   // byte index of first param byte
{
    public static FrameLayout Layout_0x51 => new(CplLenOffset: 1, FlagsOffset: 2, FeatIdxOffset: 3, FuncSwidOffset: 4, ParamsOffset: 5);
    public static FrameLayout Layout_0x50 => new(CplLenOffset: 2, FlagsOffset: 3, FeatIdxOffset: 4, FuncSwidOffset: 5, ParamsOffset: 6);

    public const int FRAME_SIZE = 64;
}
