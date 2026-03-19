using LGSTrayHID.Centurion.Transport;

namespace LGSTrayHID.Tests;

// PRO X 2 — 0x51 LONG layout (symmetric): [reportId] [cplLen] [flags] [featIdx] [func|swid] [params...]
public class FrameParsingTests_0x51
{
    private static CenturionResponse? Parse(string hexDash) =>
        CenturionTransport.ParseFrame(FrameLayout.Layout_0x51, LogFrame.Parse(hexDash), 64);

    [Fact]
    public void SymmetricResponse()
    {
        // Synthetic: reportId=0x51, cplLen=5, feat=0, func|swid=0x0A, params=[0x03,0x00]
        var result = Parse("51-05-00-00-0A-03-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00");

        Assert.NotNull(result);
        Assert.Equal(0x00, result.Value.FeatIdx);
        Assert.Equal(0x00, result.Value.FuncId);
        Assert.Equal(0x0A, result.Value.SwId);
        Assert.Equal(new byte[] { 0x03, 0x00 }, result.Value.Params);
    }

    [Fact]
    public void TooShort_ReturnsNull()
    {
        var result = CenturionTransport.ParseFrame(FrameLayout.Layout_0x51, LogFrame.Parse("51-05"), 2);
        Assert.Null(result);
    }

    [Fact]
    public void BufferNotReachingFuncSwidOffset_ReturnsNull()
    {
        // FuncSwidOffset=4; bytesRead=4 is not > 4
        var result = CenturionTransport.ParseFrame(FrameLayout.Layout_0x51, LogFrame.Parse("51-05-00-00"), 4);
        Assert.Null(result);
    }
}
