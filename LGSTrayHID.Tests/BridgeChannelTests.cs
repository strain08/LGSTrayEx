using LGSTrayHID.Centurion.Channel;
using LGSTrayHID.Centurion.Transport;
using LGSTrayHID.HidApi;

namespace LGSTrayHID.Tests;

public class BridgeChannelTests
{
    private const byte BridgeIdx = 0x0B;
    private const byte SubDeviceId = 0x01;

    // Minimal stub transport — HidDevicePtr is a readonly struct; default == IntPtr.Zero,
    // which is safe as long as we never actually send HID frames in these tests.
    private sealed class StubTransport() : CenturionTransport(default(HidDevicePtr), 0) { }

    // Extends CenturionBridgeChannel to expose _pendingRequest for test setup.
    private sealed class TestableBridgeChannel(CenturionTransport transport)
        : CenturionBridgeChannel(transport, BridgeIdx, SubDeviceId, CancellationToken.None)
    {
        public void SetPending(byte featIdx, byte funcId)
        {
            var tcs = new TaskCompletionSource<CenturionResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequest = new PendingRequest(featIdx, funcId, tcs);
        }

        public TaskCompletionSource<CenturionResponse>? GetPendingTcs() => _pendingRequest?.Tcs;
    }

    private static TestableBridgeChannel MakeChannel() =>
        new(new StubTransport());

    // Helper: build bridge params for UnwrapBridgePayload
    // subMsgLen=6, inner: feat=subFeat, func=subFunc, swId=subSwId, params=[0x01,0x01,0xAB]
    private static byte[] BridgeParams(byte subFeat, byte subFunc, byte subSwId) =>
        [0x00, 0x06, 0x00, subFeat, (byte)((subFunc << 4) | subSwId), 0x01, 0x01, 0xAB];

    // ---- TryCompleteRequest tests ----

    [Fact]
    public void TryCompleteRequest_BridgeAck_ReturnsTrue_NoPendingCompletion()
    {
        using var channel = MakeChannel();
        // Bridge sendMessage ACK: featIdx==bridgeIdx, funcId==0x01
        var frame = new CenturionResponse(BridgeIdx, 0x01, CenturionTransport.SWID, []);

        bool result = channel.TryCompleteRequest(frame);

        Assert.True(result);
        Assert.Null(channel.GetPendingTcs()); // no pending was set, nothing to complete
    }

    [Fact]
    public async Task TryCompleteRequest_MatchingResponse_CompletesPending()
    {
        using var channel = MakeChannel();
        channel.SetPending(featIdx: 0x05, funcId: 0x00);
        var tcs = channel.GetPendingTcs()!;

        // Non-bridge-ack frame matching the pending
        var frame = new CenturionResponse(0x05, 0x00, CenturionTransport.SWID, [0x03, 0x00]);

        bool result = channel.TryCompleteRequest(frame);

        Assert.True(result);
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(frame, await tcs.Task);
    }

    [Fact]
    public void TryCompleteRequest_NoPending_ReturnsFalse()
    {
        using var channel = MakeChannel();
        // No pending request; non-bridge-ack frame
        var frame = new CenturionResponse(0x05, 0x00, CenturionTransport.SWID, []);

        bool result = channel.TryCompleteRequest(frame);

        Assert.False(result);
    }

    // ---- RouteEvent tests ----

    [Fact]
    public void RouteEvent_NonBridgeFeat_PassesThrough()
    {
        using var channel = MakeChannel();
        // featIdx != bridgeIdx → not a bridge event, original frame returned
        var frame = new CenturionResponse(0x05, 0x01, 0x00, []);

        var result = channel.RouteEvent(frame);

        Assert.Equal(frame, result);
    }

    [Fact]
    public void RouteEvent_MessageEvent_SolicitedSubResponse_Consumed()
    {
        using var channel = MakeChannel();
        // Pending for inner feat=0x00, func=0x01
        channel.SetPending(featIdx: 0x00, funcId: 0x01);
        var tcs = channel.GetPendingTcs()!;

        // Outer frame: featIdx==bridgeIdx, funcId==0x01, swId==0
        // Bridge params: inner feat=0x00, func=0x01, swId=SWID (solicited)
        var outerParams = BridgeParams(subFeat: 0x00, subFunc: 0x01, subSwId: CenturionTransport.SWID);
        var frame = new CenturionResponse(BridgeIdx, 0x01, 0x00, outerParams);

        var result = channel.RouteEvent(frame);

        Assert.Null(result);          // consumed, not propagated
        Assert.True(tcs.Task.IsCompleted);
    }

    [Fact]
    public void RouteEvent_MessageEvent_UnsolicitedSubEvent_Forwarded()
    {
        using var channel = MakeChannel();
        // No pending; inner swId != SWID → unsolicited event forwarded

        // Bridge params with swId=0x0F (not SWID=0x0A)
        var outerParams = BridgeParams(subFeat: 0x00, subFunc: 0x01, subSwId: 0x0F);
        var frame = new CenturionResponse(BridgeIdx, 0x01, 0x00, outerParams);

        var result = channel.RouteEvent(frame);

        Assert.NotNull(result);
        Assert.Equal(0x0F, result.Value.SwId);
    }

    [Fact]
    public void RouteEvent_MessageEvent_MalformedPayload_Consumed()
    {
        using var channel = MakeChannel();
        // Outer matches bridge, but params are too short to unwrap (< 5 bytes)
        var frame = new CenturionResponse(BridgeIdx, 0x01, 0x00, [0x00, 0x00]);

        var result = channel.RouteEvent(frame);

        Assert.Null(result);          // consumed, malformed
    }
}
