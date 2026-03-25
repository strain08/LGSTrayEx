using LGSTrayHID.Centurion.Transport;

namespace LGSTrayHID.Tests;

// G522 — 0x50 SHORT layout: [reportId] [devAddr] [cplLen] [flags] [featIdx] [func|swid] [params...]
public class FrameParsingTests_0x50
{
    private static CenturionResponse? Parse(string hexDash) =>
        CenturionTransport.ParseFrame(FrameLayout.Layout_0x50, LogFrame.Parse(hexDash), 64);

    [Fact]
    public void BatteryStatusBroadcast()
    {
        // v3.2.0 log line 255 — G522 0x0B18, periodic unsolicited battery/status broadcast from device (sw=0x0F)
        var result = Parse("50-23-08-00-03-0F-03-E8-00-01-01-00...");

        Assert.NotNull(result);
        Assert.Equal(0x03, result.Value.FeatIdx);
        Assert.Equal(0x00, result.Value.FuncId);
        Assert.Equal(0x0F, result.Value.SwId);
        Assert.Equal(new byte[] { 0x03, 0xE8, 0x00, 0x01, 0x01 }, result.Value.Params);
    }

    [Fact]
    public void BridgeMessageEvent()
    {
        // v3.2.0 log line 283 — G522 0x0B18, bridge ROOT MessageEvent (unsolicited, sw=0x00)
        var result = Parse("50-23-0B-00-03-10-00-06-00-00-1F-01-01-62-00-...");

        Assert.NotNull(result);
        Assert.Equal(0x03, result.Value.FeatIdx);
        Assert.Equal(0x01, result.Value.FuncId);
        Assert.Equal(0x00, result.Value.SwId);
        Assert.Equal(new byte[] { 0x00, 0x06, 0x00, 0x00, 0x1F, 0x01, 0x01, 0x62 }, result.Value.Params);
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

    // Confirmed frames from v3.2.0._T3_wired_then_wireless.diagnostic_working.log (commit 4943be1)

    [Fact]
    public void GetFeatureResponse_WithSwId0x0A()
    {
        // v3.2.0 log line 213 — ROOT.getFeature(DeviceInfo=0x0100) response, sw=0x0A (our SWID)
        var result = Parse("50-23-06-00-00-0A-03-00-06-00...");

        Assert.NotNull(result);
        Assert.Equal(0x00, result.Value.FeatIdx);
        Assert.Equal(0x00, result.Value.FuncId);
        Assert.Equal(0x0A, result.Value.SwId);
        Assert.Equal(new byte[] { 0x03, 0x00, 0x06 }, result.Value.Params);
    }

    [Fact]
    public void BatteryResponse_DirectMode()
    {
        // v3.2.0 log line 223 — G522 0x0B19 wired USB, direct BatterySOC query response, sw=0x0A
        // Parsed: 78% SOC, status=2 (Charging via USB)
        var result = Parse("50-23-06-00-05-0A-4E-50-02-00...");

        Assert.NotNull(result);
        Assert.Equal(0x05, result.Value.FeatIdx);
        Assert.Equal(0x00, result.Value.FuncId);
        Assert.Equal(0x0A, result.Value.SwId);
        Assert.Equal(new byte[] { 0x4E, 0x50, 0x02 }, result.Value.Params);
    }

    [Fact]
    public void BridgeMessageEvent_BatterySolicited()
    {
        // v3.2.0 log line 389 — G522 0x0B18 wireless, bridge battery query response (solicited)
        // outer: bridge feat=0x03, MessageEvent func=1, outer swId=0x00 (MessageEvents always arrive with outer swId=0)
        // inner payload (params): subFeat=0x05, subSwId=0x0A marks this as the solicited response
        // Routing is determined by BridgeChannel.RouteEvent inspecting inner swId, not outer swId
        var result = Parse("50-23-0B-00-03-10-00-06-00-05-0A-4E-50-00-00...");

        Assert.NotNull(result);
        Assert.Equal(0x03, result.Value.FeatIdx);
        Assert.Equal(0x01, result.Value.FuncId);
        Assert.Equal(0x00, result.Value.SwId); // outer swId is always 0x00 for MessageEvents
        Assert.Equal(new byte[] { 0x00, 0x06, 0x00, 0x05, 0x0A, 0x4E, 0x50, 0x00 }, result.Value.Params);
    }

    [Fact]
    public void BridgeMessageEvent_BatteryUnsolicited()
    {
        // v3.2.0 log line 401 — G522 0x0B18 wireless, unsolicited battery event (USB cable plugged in, sw=0x00)
        // outer: bridge feat=0x03, MessageEvent func=1, swId=0x00 (device event)
        // inner: subFeat=0x05, subFunc=0, subSwId=0x00, params=[0x4E,0x50,0x01] (78%, Charging)
        var result = Parse("50-23-0B-00-03-10-00-06-00-05-00-4E-50-01-00...");

        Assert.NotNull(result);
        Assert.Equal(0x03, result.Value.FeatIdx);
        Assert.Equal(0x01, result.Value.FuncId);
        Assert.Equal(0x00, result.Value.SwId);
        Assert.Equal(new byte[] { 0x00, 0x06, 0x00, 0x05, 0x00, 0x4E, 0x50, 0x01 }, result.Value.Params);
    }

    [Fact]
    public void TooShort_ReturnsNull()
    {
        var result = CenturionTransport.ParseFrame(FrameLayout.Layout_0x50, LogFrame.Parse("50-23"), 2);
        Assert.Null(result);
    }

    [Fact]
    public void BufferNotReachingFuncSwidOffset_ReturnsNull()
    {
        // FuncSwidOffset=5; bytesRead=5 is not > 5
        var result = CenturionTransport.ParseFrame(FrameLayout.Layout_0x50, LogFrame.Parse("50-23-06-00-00"), 5);
        Assert.Null(result);
    }
}
