using LGSTrayHID.Protocol;
using LGSTrayPrimitives;

namespace LGSTrayHID.Features;

/// <summary>
/// Battery Voltage (Feature 0x1001).
/// This feature provides battery voltage in millivolts, and uses a generic 3.7V LiPo
/// discharge curve lookup table to estimate percentage.
/// </summary>
/// <remarks>
/// This is a fallback feature when 0x1000 is not available.
/// The percentage estimation is generic and may not be as accurate as device-specific
/// calculations used by Logitech G Hub.
/// </remarks>
public class BatteryVoltage : IBatteryFeature
{
    /// <inheritdoc/>
    public ushort FeatureId => HidppFeature.BATTERY_VOLTAGE;

    public string FeatureName => "Battery Voltage";

    /// <inheritdoc/>
    public async Task<BatteryUpdateReturn?> GetBatteryAsync(HidppDevice device)
    {
        // Build command using the new builder pattern
        var command = Hidpp20CommandBuilder.Create(device.DeviceIdx)
            .WithFeatureIndex(device.FeatureMap[FeatureId])
            .WithFunction(BatteryFunction.GET_STATUS)
            .Build();

        // Send command with retry logic (backoff strategy handles retries)
        Hidpp20 response = await device.Parent.WriteRead20(
            device.Parent.DevShort,
            command,
            backoffStrategy: GlobalSettings.BatteryBackoff);

        // Check if request timed out or failed after all retries
        if (response.Length == 0)
        {
            return null;
        }

        // Parse response
        // Params 0-1: Battery voltage in millivolts (16-bit big-endian)
        // Param 2: Charging status flags
        int millivolts = response.GetParam16(0);
        double percentage = LiPoVoltageCurve.EstimatePercentageFromVoltage(millivolts);
        var status = BatteryStatusParser.ParseVoltageBatteryStatus(response.GetParam(2));

        return new BatteryUpdateReturn(percentage, status, millivolts);
    }

    /// <inheritdoc/>
    public BatteryUpdateReturn? ParseBatteryEvent(Hidpp20 eventMessage)
    {
        // Validate this is an event for our feature
        if (eventMessage.GetFunctionId() != Protocol.BatteryEventFunction.BATTERY_STATUS_BROADCAST)
        {
            return null;
        }

        // Event payload format matches query response format for Feature 0x1001:
        // Params 0-1: Battery voltage in millivolts (16-bit big-endian)
        // Param 2: Charging status flags
        int millivolts = eventMessage.GetParam16(0);
        double percentage = LiPoVoltageCurve.EstimatePercentageFromVoltage(millivolts);
        var status = BatteryStatusParser.ParseVoltageBatteryStatus(eventMessage.GetParam(2));

        return new BatteryUpdateReturn(percentage, status, millivolts);
    }
}
