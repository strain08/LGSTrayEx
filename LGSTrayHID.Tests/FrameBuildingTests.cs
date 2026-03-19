using LGSTrayHID.Centurion.Transport;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace LGSTrayHID.Tests;

public class FrameBuildingTests
{
    // SWID used in TX frames
    private const byte SWID = CenturionTransport.SWID; // 0x0A
    
    [Fact]
    public void BuildFrame_0x51_Layout_ReportId_0x51()
    {        
        // Layout_0x51: CplLenOffset=1, FlagsOffset=2, FeatIdxOffset=3, FuncSwidOffset=4, ParamsOffset=5
        var frame = CenturionTransportLong.BuildFrame(FrameLayout.Layout_0x51, 0x51, 0x00, 0x00, [0x00, 0x01]);

        Assert.Equal(0x51, frame[0]);             // reportId
        Assert.Equal(5, frame[1]);                // cplLen = 3 + 2 params
        Assert.Equal(0x00, frame[2]);             // flags = FLAGS_SINGLE
        Assert.Equal(0x00, frame[3]);             // featIdx
        Assert.Equal(SWID, frame[4]);             // func=0 → (0<<4)|SWID
        Assert.Equal(0x00, frame[5]);             // param[0]
        Assert.Equal(0x01, frame[6]);             // param[1]
    }

    [Fact]
    public void BuildFrame_0x51_Layout_ReportId_0x50()
    {        
        // Same layout but different reportId byte
        var frame = CenturionTransportLong.BuildFrame(FrameLayout.Layout_0x51, 0x50, 0x00, 0x00, [0x00, 0x01]);

        Assert.Equal(0x50, frame[0]);
        Assert.Equal(5, frame[1]);
        Assert.Equal(SWID, frame[4]);
    }

    [Fact]
    public void BuildFrame_ZeroParams()
    {
        var frame = CenturionTransportLong.BuildFrame(FrameLayout.Layout_0x51, 0x51, 0x03, 0x00, []);

        Assert.Equal(3, frame[1]);                // cplLen = 3 + 0
        Assert.Equal(0x03, frame[3]);             // featIdx
        Assert.Equal(SWID, frame[4]);             // (0<<4)|SWID
    }

    [Fact]
    public void BuildFrame_IsExactly64Bytes()
    {
        var frame = CenturionTransportLong.BuildFrame(FrameLayout.Layout_0x51, 0x51, 0x00, 0x00, [0xAB]);
        Assert.Equal(64, frame.Length);
    }

    [Fact]
    public void BuildFrame_IsZeroPadded()
    {
        var frame = CenturionTransportLong.BuildFrame(FrameLayout.Layout_0x51, 0x51, 0x00, 0x00, [0xAB]);

        // ParamsOffset=5, param at [5]=0xAB; bytes [6..63] should be 0
        for (int i = 6; i < 64; i++)
            Assert.Equal(0x00, frame[i]);
    }

    [Fact]
    public void BuildFrame_FuncAndSwidEncoded()
    {
        // func=3, SWID=0x0A → funcSwid = (3<<4)|0x0A = 0x3A
        var frame = CenturionTransportLong.BuildFrame(FrameLayout.Layout_0x51, 0x51, 0x00, 0x03, []);
        Assert.Equal(0x3A, frame[4]);
    }
}
