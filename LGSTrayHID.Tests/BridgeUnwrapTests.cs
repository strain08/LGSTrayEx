using LGSTrayHID.Centurion.Transport;

namespace LGSTrayHID.Tests;

public class BridgeUnwrapTests
{
    // Real G522 bridge params extracted from v3.2.0 log line 283 (commit 4943be1):
    // Outer frame 50-23-0B-00-03-10-00-06-00-00-1F-01-01-62-00...
    // Parsed via 0x50 layout → params=[0x00,0x06,0x00,0x00,0x1F,0x01,0x01,0x62]
    private static readonly byte[] RealG522BridgeParams = [0x00, 0x06, 0x00, 0x00, 0x1F, 0x01, 0x01, 0x62];

    [Fact]
    public void Unwrap_RealG522Frame()
    {
        // subMsgLen=6, subFeatIdx=0x00, subFuncSwid=0x1F (func=1, swId=0xF), subParams=[0x01,0x01,0x62]
        var result = CenturionTransport.UnwrapBridgePayload(RealG522BridgeParams);

        Assert.NotNull(result);
        Assert.Equal(0x00, result.Value.FeatIdx);
        Assert.Equal(0x01, result.Value.FuncId);
        Assert.Equal(0x0F, result.Value.SwId);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x62 }, result.Value.Params);
    }

    [Fact]
    public void Unwrap_SolicitedSubResponse_SwId0x0A()
    {
        // subFuncSwid = (func=1, swId=SWID=0x0A) = (1<<4)|0x0A = 0x1A
        var bridgeParams = new byte[] { 0x00, 0x06, 0x00, 0x00, 0x1A, 0x01, 0x01, 0x62 };

        var result = CenturionTransport.UnwrapBridgePayload(bridgeParams);

        Assert.NotNull(result);
        Assert.Equal(0x00, result.Value.FeatIdx);
        Assert.Equal(0x01, result.Value.FuncId);
        Assert.Equal(0x0A, result.Value.SwId);
    }

    // Confirmed frames from v3.2.0._T3_wired_then_wireless.diagnostic_working.log (commit 4943be1)

    [Fact]
    public void Unwrap_BatteryResponse_SubFeat5_Solicited()
    {
        // Inner payload of v3.2.0 log line 389: bridge battery query response
        // outer frame: 50-23-0B-00-03-10-00-06-00-05-0A-4E-50-00
        // params extracted via 0x50 layout: [0x00,0x06,0x00,0x05,0x0A,0x4E,0x50,0x00]
        // subMsgLen=6, subFeat=0x05 (BatterySOC), subFunc=0, subSwId=0x0A, params=[0x4E,0x50,0x00]
        var bridgeParams = new byte[] { 0x00, 0x06, 0x00, 0x05, 0x0A, 0x4E, 0x50, 0x00 };

        var result = CenturionTransport.UnwrapBridgePayload(bridgeParams);

        Assert.NotNull(result);
        Assert.Equal(0x05, result.Value.FeatIdx);
        Assert.Equal(0x00, result.Value.FuncId);
        Assert.Equal(0x0A, result.Value.SwId);
        Assert.Equal(new byte[] { 0x4E, 0x50, 0x00 }, result.Value.Params);
    }

    [Fact]
    public void Unwrap_BatteryEvent_SubFeat5_Unsolicited()
    {
        // Inner payload of v3.2.0 log line 401: unsolicited battery event (USB cable plugged in)
        // outer frame: 50-23-0B-00-03-10-00-06-00-05-00-4E-50-01
        // params extracted via 0x50 layout: [0x00,0x06,0x00,0x05,0x00,0x4E,0x50,0x01]
        // subMsgLen=6, subFeat=0x05 (BatterySOC), subFunc=0, subSwId=0x00 (device event), params=[0x4E,0x50,0x01]
        var bridgeParams = new byte[] { 0x00, 0x06, 0x00, 0x05, 0x00, 0x4E, 0x50, 0x01 };

        var result = CenturionTransport.UnwrapBridgePayload(bridgeParams);

        Assert.NotNull(result);
        Assert.Equal(0x05, result.Value.FeatIdx);
        Assert.Equal(0x00, result.Value.FuncId);
        Assert.Equal(0x00, result.Value.SwId);
        Assert.Equal(new byte[] { 0x4E, 0x50, 0x01 }, result.Value.Params);
    }

    [Fact]
    public void Unwrap_TooShort_ReturnsNull()
    {
        // Less than 5 bytes → null
        var result = CenturionTransport.UnwrapBridgePayload([0x00, 0x06, 0x00, 0x00]);
        Assert.Null(result);
    }

    [Fact]
    public void Unwrap_SubMsgLenLessThan3_ReturnsNull()
    {
        // subMsgLen=2 < 3 → null
        var bridgeParams = new byte[] { 0x00, 0x02, 0x00, 0x00, 0x1F };
        var result = CenturionTransport.UnwrapBridgePayload(bridgeParams);
        Assert.Null(result);
    }

    [Fact]
    public void Unwrap_SubMsgLenExceedsBuffer_ReturnsNull()
    {
        // Claim subMsgLen=100 but only 5 bytes available
        var bridgeParams = new byte[] { 0x00, 100, 0x00, 0x00, 0x1F };
        var result = CenturionTransport.UnwrapBridgePayload(bridgeParams);
        Assert.Null(result);
    }

    [Fact]
    public void Unwrap_NoParams()
    {
        // subMsgLen=3 exactly → subParamLen=0, params=[]
        var bridgeParams = new byte[] { 0x00, 0x03, 0x00, 0x00, 0x1F };
        var result = CenturionTransport.UnwrapBridgePayload(bridgeParams);

        Assert.NotNull(result);
        Assert.Empty(result.Value.Params);
    }
}
