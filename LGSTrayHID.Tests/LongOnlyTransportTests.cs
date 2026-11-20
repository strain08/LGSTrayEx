using LGSTrayHID.HidApi;
using LGSTrayHID.Protocol;

namespace LGSTrayHID.Tests;

/// <summary>
/// Issue #31: the newer G733 Lightspeed dongle (PID 0x0AFE) exposes only a LONG HID++ collection
/// (page 0xFF43 / usage 0x0202) - no SHORT collection. The receiver must be able to start on the
/// long channel alone, and every request path must tolerate an absent (zero) short handle.
/// </summary>
public class LongOnlyTransportTests
{
    // ---- Start gate ----

    [Fact]
    public void IsTransportReady_BothChannels_True()
    {
        Assert.True(HidppReceiver.IsTransportReady(hasShort: true, hasLong: true, longOnly: false));
    }

    [Fact]
    public void IsTransportReady_LongOnly_WithoutLongOnlyMode_False()
    {
        // Short partner may still be on its way (G733 0x0AB5: LONG arrives before SHORT).
        Assert.False(HidppReceiver.IsTransportReady(hasShort: false, hasLong: true, longOnly: false));
    }

    [Fact]
    public void IsTransportReady_LongOnly_WithLongOnlyMode_True()
    {
        // No SHORT sibling exists in the container (G733 0x0AFE).
        Assert.True(HidppReceiver.IsTransportReady(hasShort: false, hasLong: true, longOnly: true));
    }

    [Fact]
    public void IsTransportReady_ShortOnly_False()
    {
        Assert.False(HidppReceiver.IsTransportReady(hasShort: true, hasLong: false, longOnly: false));
        Assert.False(HidppReceiver.IsTransportReady(hasShort: true, hasLong: false, longOnly: true));
    }

    [Fact]
    public void IsTransportReady_NoChannels_False()
    {
        Assert.False(HidppReceiver.IsTransportReady(hasShort: false, hasLong: false, longOnly: true));
    }

    // ---- Zero-handle guards: must return "no response" without touching hidapi ----

    [Fact]
    public async Task WriteRead10_ZeroHandle_ReturnsEmpty()
    {
        using var receiver = new HidppReceiver();

        byte[] ret = await receiver.WriteRead10(IntPtr.Zero, Hidpp10Commands.QueryDeviceCount(), timeout: 50);

        Assert.Empty(ret);
    }

    [Fact]
    public async Task WriteRead20_ZeroHandle_ReturnsEmpty()
    {
        using var receiver = new HidppReceiver();

        Hidpp20 ret = await receiver.WriteRead20(IntPtr.Zero, Hidpp20Commands.Ping(0xFF, 0x55), timeout: 50);

        Assert.Equal(0, ret.Length);
    }

    [Fact]
    public async Task Ping20_ZeroChannel_ReturnsFalse()
    {
        using var receiver = new HidppReceiver();

        bool ok = await receiver.Ping20(0xFF, timeout: 50, ignoreHIDPP10: false, channel: (HidDevicePtr)IntPtr.Zero);

        Assert.False(ok);
    }
}
