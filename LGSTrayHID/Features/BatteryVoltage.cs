using LGSTrayPrimitives;
using LGSTrayHID.Protocol;

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
    private const int QueryTimeout = 5000;
    public ushort FeatureId => HidppFeature.BATTERY_VOLTAGE;

    public string FeatureName => "Battery Voltage";

    /// <summary>
    /// Generic 3.7V LiPo discharge curve lookup table.
    /// Maps battery voltage (in millivolts) to estimated percentage.
    /// Index 0 = 100%, Index 99 = 1%.
    /// </summary>
    private static readonly int[] VoltageLookupTable = [
        4186, 4156, 4143, 4133, 4122, 4113, 4103, 4094, 4086, 4075,
        4067, 4059, 4051, 4043, 4035, 4027, 4019, 4011, 4003, 3997,
        3989, 3983, 3976, 3969, 3961, 3955, 3949, 3942, 3935, 3929,
        3922, 3916, 3909, 3902, 3896, 3890, 3883, 3877, 3870, 3865,
        3859, 3853, 3848, 3842, 3837, 3833, 3828, 3824, 3819, 3815,
        3811, 3808, 3804, 3800, 3797, 3793, 3790, 3787, 3784, 3781,
        3778, 3775, 3772, 3770, 3767, 3764, 3762, 3759, 3757, 3754,
        3751, 3748, 3744, 3741, 3737, 3734, 3730, 3726, 3724, 3720,
        3717, 3714, 3710, 3706, 3702, 3697, 3693, 3688, 3683, 3677,
        3671, 3666, 3662, 3658, 3654, 3646, 3633, 3612, 3579, 3537
    ];

    /// <inheritdoc/>
    public async Task<BatteryUpdateReturn?> GetBatteryAsync(HidppDevice device)
    {
        // Build command using the new builder pattern
        var command = Hidpp20CommandBuilder.Create(device.DeviceIdx)
            .WithFeatureIndex(device.FeatureMap[FeatureId])
            .WithFunction(BatteryFunction.GET_STATUS)
            .Build();

        // Send command and wait for response
        Hidpp20 response = await device.Parent.WriteRead20(
            device.Parent.DevShort,
            command, 
            QueryTimeout);

        // Check if request timed out or failed
        if (response.Length == 0)
        {
            return null;
        }

        // Parse response
        // Params 0-1: Battery voltage in millivolts (16-bit big-endian)
        // Param 2: Charging status flags
        int millivolts = response.GetParam16(0);
        double percentage = EstimatePercentageFromVoltage(millivolts);
        var status = BatteryStatusParser.ParseVoltageBatteryStatus(response.GetParam(2));

        return new BatteryUpdateReturn(percentage, status, millivolts);
    }

    /// <summary>
    /// Estimate battery percentage from voltage using the lookup table.
    /// </summary>
    /// <param name="millivolts">Battery voltage in millivolts</param>
    /// <returns>Estimated percentage (0-100)</returns>
    private static double EstimatePercentageFromVoltage(int millivolts)
    {
        // Find the first voltage in the table that is lower than the measured voltage
        for (int i = 0; i < VoltageLookupTable.Length; i++)
        {
            if (millivolts > VoltageLookupTable[i])
            {
                // Return remaining percentage based on position in table
                return VoltageLookupTable.Length - i;
            }
        }

        // Voltage is below the lowest value in the table
        return 0;
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
        double percentage = EstimatePercentageFromVoltage(millivolts);
        var status = BatteryStatusParser.ParseVoltageBatteryStatus(eventMessage.GetParam(2));

        return new BatteryUpdateReturn(percentage, status, millivolts);
    }
}
