using LGSTrayHID.Centurion.Transport;

namespace LGSTrayHID.Tests;

public class BridgeUnwrapTests
{
    // Real G522 bridge params extracted from log line 116:
    // Outer frame 50-23-0B-00-03-10-00-06-00-00-1F-01-01-AB-00...
    // Parsed via 0x50 layout → params=[0x00,0x06,0x00,0x00,0x1F,0x01,0x01,0xAB]
    private static readonly byte[] RealG522BridgeParams = [0x00, 0x06, 0x00, 0x00, 0x1F, 0x01, 0x01, 0xAB];

    [Fact]
    public void Unwrap_RealG522Frame()
    {
        // subMsgLen=6, subFeatIdx=0x00, subFuncSwid=0x1F (func=1, swId=0xF), subParams=[0x01,0x01,0xAB]
        var result = CenturionTransport.UnwrapBridgePayload(RealG522BridgeParams);

        Assert.NotNull(result);
        Assert.Equal(0x00, result.Value.FeatIdx);
        Assert.Equal(0x01, result.Value.FuncId);
        Assert.Equal(0x0F, result.Value.SwId);
        Assert.Equal(new byte[] { 0x01, 0x01, 0xAB }, result.Value.Params);
    }

    [Fact]
    public void Unwrap_SolicitedSubResponse_SwId0x0A()
    {
        // Same but subFuncSwid = (func=1, swId=SWID=0x0A) = (1<<4)|0x0A = 0x1A
        var bridgeParams = new byte[] { 0x00, 0x06, 0x00, 0x00, 0x1A, 0x01, 0x01, 0xAB };

        var result = CenturionTransport.UnwrapBridgePayload(bridgeParams);

        Assert.NotNull(result);
        Assert.Equal(0x00, result.Value.FeatIdx);
        Assert.Equal(0x01, result.Value.FuncId);
        Assert.Equal(0x0A, result.Value.SwId);
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
