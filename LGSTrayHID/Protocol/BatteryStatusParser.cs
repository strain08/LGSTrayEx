using LGSTrayPrimitives;
using static LGSTrayPrimitives.PowerSupplyStatus;

namespace LGSTrayHID.Protocol;

/// <summary>
/// Centralized parser for battery status codes from different HID++ battery features.
/// Different battery features (0x1000, 0x1001, 0x1004) use different encoding schemes
/// for charging status, so this class provides dedicated parsers for each.
/// </summary>
public static class BatteryStatusParser
{
    /// <summary>
    /// Parse battery status from HID++ Unified Battery features (0x1000 and 0x1004).
    /// These features use a single status byte with standardized codes.
    /// </summary>
    /// <param name="statusByte">Status byte from response parameter 2</param>
    /// <returns>PowerSupplyStatus enum value</returns>
    /// <example>
    /// // Before:
    /// // var status = ret.GetParam(2) switch {
    /// //     0 => POWER_SUPPLY_STATUS_DISCHARGING,
    /// //     1 or 2 => POWER_SUPPLY_STATUS_CHARGING,
    /// //     ...
    /// // };
    ///
    /// // After:
    /// var status = BatteryStatusParser.ParseUnifiedBatteryStatus(ret.GetParam(2));
    /// </example>
    public static PowerSupplyStatus ParseUnifiedBatteryStatus(byte statusByte)
    {
        return statusByte switch
        {
            BatteryStatusCode.DISCHARGING => POWER_SUPPLY_STATUS_DISCHARGING,
            BatteryStatusCode.CHARGING or
            BatteryStatusCode.CHARGING_SLOW or
            BatteryStatusCode.CHARGING_ERROR => POWER_SUPPLY_STATUS_CHARGING,
            BatteryStatusCode.FULL => POWER_SUPPLY_STATUS_FULL,
            _ => POWER_SUPPLY_STATUS_NOT_CHARGING,
        };
    }

    /// <summary>
    /// Parse battery status from HID++ Battery Voltage feature (0x1001).
    /// This feature uses a flags byte where bit 7 indicates charging,
    /// and bits 0-2 encode the specific charging state.
    /// </summary>
    /// <param name="flags">Flags byte from response parameter 2</param>
    /// <returns>PowerSupplyStatus enum value</returns>
    /// <remarks>
    /// Encoding:
    /// - Bit 7 (0x80): Charging flag
    ///   - If set: Check bits 0-2 for charging state (0=charging, 1=full, 2=not charging)
    ///   - If clear: Battery is discharging
    /// </remarks>
    public static PowerSupplyStatus ParseVoltageBatteryStatus(byte flags)
    {
        // Check if charging flag is set (bit 7)
        if ((flags & BatteryStatusCode.CHARGING_FLAG) > 0)
        {
            // Charging flag is set - check sub-status in bits 0-2
            return (flags & BatteryStatusCode.STATUS_MASK) switch
            {
                0 => POWER_SUPPLY_STATUS_CHARGING,
                1 => POWER_SUPPLY_STATUS_FULL,
                2 => POWER_SUPPLY_STATUS_NOT_CHARGING,
                _ => POWER_SUPPLY_STATUS_UNKNOWN,
            };
        }

        // Charging flag not set - battery is discharging
        return POWER_SUPPLY_STATUS_DISCHARGING;
    }
}
