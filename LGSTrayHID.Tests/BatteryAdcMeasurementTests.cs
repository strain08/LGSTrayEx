using LGSTrayHID.Features;
using LGSTrayHID.Protocol;
using LGSTrayPrimitives;
using Xunit;

namespace LGSTrayHID.Tests;

public class BatteryAdcMeasurementTests
{
    private readonly BatteryAdcMeasurement _adcFeature = new();

    [Fact]
    public void LiPoVoltageCurve_ThresholdBoundaries()
    {
        // Test that exact boundaries return correct percentage (inclusive boundary check)
        Assert.Equal(100, LiPoVoltageCurve.EstimatePercentageFromVoltage(4186));
        Assert.Equal(90, LiPoVoltageCurve.EstimatePercentageFromVoltage(4067));
        Assert.Equal(80, LiPoVoltageCurve.EstimatePercentageFromVoltage(3989));
        Assert.Equal(50, LiPoVoltageCurve.EstimatePercentageFromVoltage(3811));
        Assert.Equal(10, LiPoVoltageCurve.EstimatePercentageFromVoltage(3671));
        
        // Under 3500 mV should return 0%
        Assert.Equal(0, LiPoVoltageCurve.EstimatePercentageFromVoltage(3500));
        Assert.Equal(0, LiPoVoltageCurve.EstimatePercentageFromVoltage(3400));
        
        // Above 4186 should stay 100%
        Assert.Equal(100, LiPoVoltageCurve.EstimatePercentageFromVoltage(4200));
    }

    [Fact]
    public void ParseBatteryEvent_ValidActiveDischarging()
    {
        // Construct a valid broadcast frame:
        // Byte 0: 0x11 (long message prefix)
        // Byte 1: 0x01 (device index)
        // Byte 2: 0x08 (dummy battery feature index)
        // Byte 3: 0x00 (function index 0x00, software ID 0x00)
        // Params 0-1 (buffer[4-5]): 4058 mV (0x0FDA)
        // Param 2 (buffer[6]): 0x01 (valid = 1, charging = 0)
        var buffer = new byte[20];
        buffer[0] = 0x11;
        buffer[1] = 0x01;
        buffer[2] = 0x08;
        buffer[3] = 0x00; // func = 0x00, swId = 0
        buffer[4] = 0x0F; // 4058 high byte
        buffer[5] = 0xDA; // 4058 low byte
        buffer[6] = 0x01; // Flags: measurement valid

        var eventMsg = new Hidpp20(buffer);
        var update = _adcFeature.ParseBatteryEvent(eventMsg);

        Assert.NotNull(update);
        Assert.Equal(PowerSupplyStatus.DISCHARGING, update.Value.status);
        Assert.Equal(4058, update.Value.batteryMVolt);
        // Voltage 4058 mV is between 4059 mV (89% index 11) and 4051 mV (88% index 12), mapping to 88% in the lookup table.
        Assert.Equal(88, update.Value.batteryPercentage);
    }

    [Fact]
    public void ParseBatteryEvent_ValidActiveCharging()
    {
        // Construct a valid broadcast frame for charging:
        // Params 0-1: 3867 mV (0x0F1B)
        // Param 2: 0x03 (valid = 1, charging = 1)
        var buffer = new byte[20];
        buffer[0] = 0x11;
        buffer[1] = 0x01;
        buffer[2] = 0x08;
        buffer[3] = 0x00;
        buffer[4] = 0x0F; // 3867 high byte
        buffer[5] = 0x1B; // 3867 low byte
        buffer[6] = 0x03; // Flags: measurement valid | charging

        var eventMsg = new Hidpp20(buffer);
        var update = _adcFeature.ParseBatteryEvent(eventMsg);

        Assert.NotNull(update);
        Assert.Equal(PowerSupplyStatus.CHARGING, update.Value.status);
        Assert.Equal(3867, update.Value.batteryMVolt);
    }

    [Fact]
    public void ParseBatteryEvent_InvalidMeasurementReturnsNull()
    {
        // Construct a broadcast frame with valid bit clear:
        // Param 2: 0x00 (valid = 0)
        var buffer = new byte[20];
        buffer[0] = 0x11;
        buffer[1] = 0x01;
        buffer[2] = 0x08;
        buffer[3] = 0x00;
        buffer[4] = 0x0F;
        buffer[5] = 0xAA;
        buffer[6] = 0x00; // Flags: invalid

        var eventMsg = new Hidpp20(buffer);
        var update = _adcFeature.ParseBatteryEvent(eventMsg);

        Assert.Null(update);
    }

    [Fact]
    public void IsOfflineEvent_CorrectlyIdentifiesOfflineState()
    {
        // Broadcast frame with valid bit clear -> should be offline event
        var bufferOffline = new byte[20];
        bufferOffline[0] = 0x11;
        bufferOffline[1] = 0x01;
        bufferOffline[2] = 0x08;
        bufferOffline[3] = 0x00; // func = 0x00
        bufferOffline[6] = 0x00; // Flags: valid bit clear
        Assert.True(_adcFeature.IsOfflineEvent(new Hidpp20(bufferOffline)));

        // Broadcast frame with valid bit set -> should NOT be offline event
        var bufferOnline = new byte[20];
        bufferOnline[0] = 0x11;
        bufferOnline[1] = 0x01;
        bufferOnline[2] = 0x08;
        bufferOnline[3] = 0x00; // func = 0x00
        bufferOnline[6] = 0x01; // Flags: valid bit set
        Assert.False(_adcFeature.IsOfflineEvent(new Hidpp20(bufferOnline)));

        // Non-broadcast function -> should NOT be offline event
        var bufferWrongFunc = new byte[20];
        bufferWrongFunc[0] = 0x11;
        bufferWrongFunc[1] = 0x01;
        bufferWrongFunc[2] = 0x08;
        bufferWrongFunc[3] = 0x10; // func = 0x01 (not 0x00)
        bufferWrongFunc[6] = 0x00; // Flags: valid bit clear
        Assert.False(_adcFeature.IsOfflineEvent(new Hidpp20(bufferWrongFunc)));
    }
}
