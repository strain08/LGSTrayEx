using LGSTrayHID.Centurion.Transport;

namespace LGSTrayHID.Tests;

// G522 — 0x50 SHORT layout: [reportId] [devAddr] [cplLen] [flags] [featIdx] [func|swid] [params...]
public class FrameParsingTests_0x50
{
    private static CenturionResponse? Parse(string hexDash) =>
        CenturionTransport.ParseFrame(FrameLayout.Layout_0x50_RxOnly, LogFrame.Parse(hexDash), 64);

    [Fact]
    public void GetFeatureResponse()
    {
        var result = Parse("50-23-06-00-00-0F-03-00-01...");

        Assert.NotNull(result);
        Assert.Equal(0x00, result.Value.FeatIdx);
        Assert.Equal(0x00, result.Value.FuncId);
        Assert.Equal(0x0F, result.Value.SwId);
        Assert.Equal(new byte[] { 0x03, 0x00, 0x01 }, result.Value.Params);
    }

    [Fact]
    public void BatteryEvent()
    {
        var result = Parse("50-23-08-00-03-0F-03-E8-00-00-01-00...");

        Assert.NotNull(result);
        Assert.Equal(0x03, result.Value.FeatIdx);
        Assert.Equal(0x00, result.Value.FuncId);
        Assert.Equal(0x0F, result.Value.SwId);
        Assert.Equal(new byte[] { 0x03, 0xE8, 0x00, 0x00, 0x01 }, result.Value.Params);
    }

    [Fact]
    public void BridgeMessageEvent()
    {
        var result = Parse("50-23-0B-00-03-10-00-06-00-00-1F-01-01-AB-00-...");

        Assert.NotNull(result);
        Assert.Equal(0x03, result.Value.FeatIdx);
        Assert.Equal(0x01, result.Value.FuncId);
        Assert.Equal(0x00, result.Value.SwId);
        Assert.Equal(new byte[] { 0x00, 0x06, 0x00, 0x00, 0x1F, 0x01, 0x01, 0xAB }, result.Value.Params);
    }

    [Fact]
    public void UnsolicitedFrame_FeatFF()
    {
        var result = Parse("50-23-05-00-FF-00-0F-05-00-...");

        Assert.NotNull(result);
        Assert.Equal(0xFF, result.Value.FeatIdx);
        Assert.Equal(0x00, result.Value.FuncId);
        Assert.Equal(0x00, result.Value.SwId);
        Assert.Equal(new byte[] { 0x0F, 0x05 }, result.Value.Params);
    }

    [Fact]
    public void TooShort_ReturnsNull()
    {
        var result = CenturionTransport.ParseFrame(FrameLayout.Layout_0x50_RxOnly, LogFrame.Parse("50-23"), 2);
        Assert.Null(result);
    }

    [Fact]
    public void BufferNotReachingFuncSwidOffset_ReturnsNull()
    {
        // FuncSwidOffset=5; bytesRead=5 is not > 5
        var result = CenturionTransport.ParseFrame(FrameLayout.Layout_0x50_RxOnly, LogFrame.Parse("50-23-06-00-00"), 5);
        Assert.Null(result);
    }
}
