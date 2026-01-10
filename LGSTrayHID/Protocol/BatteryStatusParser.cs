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
            BatteryStatusCode.DISCHARGING => DISCHARGING,
            BatteryStatusCode.CHARGING or
            BatteryStatusCode.CHARGING_SLOW or
            BatteryStatusCode.CHARGING_ERROR => CHARGING,
            BatteryStatusCode.FULL => FULL,
            _ => NOT_CHARGING,
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
                0 => CHARGING,
                1 => FULL,
                2 => NOT_CHARGING,
                _ => UNKNOWN,
            };
        }

        // Charging flag not set - battery is discharging
        return DISCHARGING;
    }

    /// <summary>
    /// Validate battery level flags from HID++ Unified Battery features (0x1000 and 0x1004).
    /// Battery level flags should have exactly ONE bit set (mutually exclusive).
    /// </summary>
    /// <param name="levelFlags">Level flags byte from response parameter 1</param>
    /// <returns>True if flags are valid, false if corrupt/invalid</returns>
    /// <remarks>
    /// (!) Not used for Feature 1001 (Voltage), which uses a different encoding.
    /// Valid flags (only one bit set in lower nibble):
    /// - 0x01: Critical
    /// - 0x02: Low
    /// - 0x04: Good
    /// - 0x08: Full
    ///
    /// Invalid examples:
    /// - 0x00: No flags set (uninitialized)
    /// - 0x0F: All flags set (corrupt data, seen during device wake/init)
    /// - 0x03, 0x05, etc.: Multiple flags set (protocol violation)
    /// </remarks>
    public static bool IsValidBatteryLevelFlags(byte levelFlags)
    {
        // Mask to lower 4 bits (bits 0-3)
        byte flags = (byte)(levelFlags & 0x0F);

        // Check if exactly one bit is set using bit manipulation
        // A number has exactly one bit set if: (n & (n-1)) == 0 and n != 0
        // Examples:
        // - 0x01 (0b0001): 0x01 & 0x00 = 0x00  Valid
        // - 0x04 (0b0100): 0x04 & 0x03 = 0x00  Valid
        // - 0x0F (0b1111): 0x0F & 0x0E = 0x0E  Invalid (multiple bits)
        // - 0x00 (0b0000): Special case        Invalid (no bits)
        return flags != 0 && (flags & (flags - 1)) == 0;
    }
}
