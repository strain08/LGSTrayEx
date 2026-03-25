using LGSTrayHID.Centurion.Features;
using LGSTrayHID.Features;
using LGSTrayPrimitives;

namespace LGSTrayHID.Tests;

public class BatterySOCTests
{
    private static readonly CenturionBatterySOC Feature = new(featureIndex: 0, featureId: 0x0104);

    [Fact]
    public void Parse_Discharging_75pct()
    {
        var result = Feature.ParseBatteryParams([75, 75, 0]);

        Assert.NotNull(result);
        Assert.Equal(75, result.Value.batteryPercentage);
        Assert.Equal(PowerSupplyStatus.DISCHARGING, result.Value.status);
    }

    [Fact]
    public void Parse_Charging()
    {
        var result = Feature.ParseBatteryParams([50, 50, 1]);

        Assert.NotNull(result);
        Assert.Equal(50, result.Value.batteryPercentage);
        Assert.Equal(PowerSupplyStatus.CHARGING, result.Value.status);
    }

    [Fact]
    public void Parse_UsbCharging()
    {
        var result = Feature.ParseBatteryParams([50, 50, 2]);

        Assert.NotNull(result);
        Assert.Equal(50, result.Value.batteryPercentage);
        Assert.Equal(PowerSupplyStatus.CHARGING, result.Value.status);
    }

    [Fact]
    public void Parse_Full()
    {
        var result = Feature.ParseBatteryParams([100, 100, 3]);

        Assert.NotNull(result);
        Assert.Equal(100, result.Value.batteryPercentage);
        Assert.Equal(PowerSupplyStatus.FULL, result.Value.status);
    }

    [Fact]
    public void Parse_Clamped_Over100()
    {
        var result = Feature.ParseBatteryParams([150, 150, 0]);

        Assert.NotNull(result);
        Assert.Equal(100, result.Value.batteryPercentage);
        Assert.Equal(PowerSupplyStatus.DISCHARGING, result.Value.status);
    }

    [Fact]
    public void Parse_UnknownCharging()
    {
        var result = Feature.ParseBatteryParams([80, 80, 255]);

        Assert.NotNull(result);
        Assert.Equal(80, result.Value.batteryPercentage);
        Assert.Equal(PowerSupplyStatus.UNKNOWN, result.Value.status);
    }

    // Confirmed params from v3.2.0._T3_wired_then_wireless.diagnostic_working.log (commit 4943be1)
    // Real frames have param[0] ≠ param[1] (78 vs 80); existing tests use equal pairs.

    [Fact]
    public void Parse_RealFrame_WiredCharging()
    {
        // v3.2.0 log line 224 — G522 0x0B19 wired USB, status=2 (Charging via USB)
        var result = Feature.ParseBatteryParams([0x4E, 0x50, 0x02]);

        Assert.NotNull(result);
        Assert.Equal(78, result.Value.batteryPercentage);
        Assert.Equal(PowerSupplyStatus.CHARGING, result.Value.status);
    }

    [Fact]
    public void Parse_RealFrame_WirelessDischarging()
    {
        // v3.2.0 log line 390 — G522 0x0B18 wireless, status=0 (Discharging)
        var result = Feature.ParseBatteryParams([0x4E, 0x50, 0x00]);

        Assert.NotNull(result);
        Assert.Equal(78, result.Value.batteryPercentage);
        Assert.Equal(PowerSupplyStatus.DISCHARGING, result.Value.status);
    }

    [Fact]
    public void Parse_TooShort_ReturnsNull()
    {
        var result = Feature.ParseBatteryParams([75, 75]);
        Assert.Null(result);
    }

    [Fact]
    public void Parse_Empty_ReturnsNull()
    {
        var result = Feature.ParseBatteryParams([]);
        Assert.Null(result);
    }
}
