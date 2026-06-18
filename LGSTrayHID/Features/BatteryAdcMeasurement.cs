using LGSTrayHID.Protocol;
using LGSTrayPrimitives;
using static LGSTrayPrimitives.PowerSupplyStatus;

namespace LGSTrayHID.Features;

/// <summary>
/// ADC Measurement (Feature 0x1F20).
/// Used by direct-connect Logitech gaming headsets (G733, G535, G935, PRO X Wireless) which
/// expose battery status only through this feature. Reports battery voltage in millivolts and
/// uses the generic 3.7V LiPo discharge curve to estimate percentage.
/// </summary>
/// <remarks>
/// Modeled on Solaar's decipher_adc_measurement (hidpp20.py). Unlike the receiver-attached
/// 0x1000/0x1001/0x1004 features, 0x1F20 devices are direct-connect HID++ 2.0 devices, so the
/// request is written on the LONG channel (DevLong).
///
/// Deep-sleep / wireless-bridge safety: a Lightspeed dongle may buffer an all-zeros reply for a
/// sleeping headset instead of dropping the request. The flags bit0 (measurement-valid) check
/// and the voltage sanity floor below reject such frames so we never report a false 0%.
/// </remarks>
public class BatteryAdcMeasurement : IBatteryFeature
{
    /// <summary>Flags byte bit0: measurement is valid / sub-device is active.</summary>
    private const byte FLAG_MEASUREMENT_VALID = 0x01;

    /// <summary>Flags byte bit1: battery is charging (recharging) rather than discharging.</summary>
    private const byte FLAG_CHARGING = 0x02;

    /// <summary>
    /// Minimum plausible cell voltage (mV). A live LiPo cell never reads near zero; anything
    /// below this is treated as a buffered/garbage frame and rejected.
    /// </summary>
    private const int MIN_PLAUSIBLE_MV = 2000;

    /// <inheritdoc/>
    public ushort FeatureId => HidppFeature.ADC_MEASUREMENT;

    public string FeatureName => "ADC Measurement";

    /// <inheritdoc/>
    public async Task<BatteryUpdateReturn?> GetBatteryAsync(HidppDevice device)
    {
        var command = Hidpp20CommandBuilder.Create(device.DeviceIdx)
            .WithFeatureIndex(device.FeatureMap[FeatureId])
            .WithFunction(BatteryFunction.GET_STATUS)
            .Build();

        // Direct-connect HID++ 2.0 devices use the LONG channel (see class remarks).
        Hidpp20 response = await device.Parent.WriteRead20(
            device.Parent.DevLong,
            command,
            backoffStrategy: GlobalSettings.BatteryBackoff);

        // Timed out or failed after all retries (covers headset fully off / HID++ error reply).
        if (response.Length == 0)
        {
            return null;
        }

        return ParseAdcMeasurement(response.GetParam16(0), response.GetParam(2));
    }

    /// <inheritdoc/>
    public BatteryUpdateReturn? ParseBatteryEvent(Hidpp20 eventMessage)
    {
        // Validate this is an event for our feature
        if (eventMessage.GetFunctionId() != BatteryEventFunction.BATTERY_STATUS_BROADCAST)
        {
            return null;
        }

        // Event payload format matches the query response format for Feature 0x1F20:
        // Params 0-1: Battery voltage in millivolts (16-bit big-endian)
        // Param 2: Flags byte (bit0 valid, bit1 charging)
        return ParseAdcMeasurement(eventMessage.GetParam16(0), eventMessage.GetParam(2));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 0x1F20 is also used to announce the headset becoming inactive: when the valid bit (bit0)
    /// is cleared, Solaar marks the device offline (notifications.py: present = False / active = False).
    /// Only events for our feature qualify.
    /// </remarks>
    public bool IsOfflineEvent(Hidpp20 eventMessage)
    {
        if (eventMessage.GetFunctionId() != BatteryEventFunction.BATTERY_STATUS_BROADCAST)
        {
            return false;
        }

        byte flags = eventMessage.GetParam(2);
        return (flags & FLAG_MEASUREMENT_VALID) == 0;
    }

    /// <summary>
    /// Parse a 0x1F20 measurement into a battery update, applying the deep-sleep guards.
    /// Returns null when the measurement is invalid (asleep headset / buffered-zeros frame).
    /// </summary>
    private static BatteryUpdateReturn? ParseAdcMeasurement(int millivolts, byte flags)
    {
        // Guard 1: bit0 clear means the measurement is invalid / sub-device asleep.
        // An all-zeros buffered frame (flags == 0x00) is rejected here.
        if ((flags & FLAG_MEASUREMENT_VALID) == 0)
        {
            return null;
        }

        // Guard 2: voltage sanity floor against partially-buffered junk frames.
        if (millivolts < MIN_PLAUSIBLE_MV)
        {
            return null;
        }

        double percentage = LiPoVoltageCurve.EstimatePercentageFromVoltage(millivolts);
        var status = (flags & FLAG_CHARGING) != 0 ? CHARGING : DISCHARGING;

        return new BatteryUpdateReturn(percentage, status, millivolts);
    }
}
