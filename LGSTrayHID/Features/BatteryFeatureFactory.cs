using LGSTrayHID.Protocol;

namespace LGSTrayHID.Features;

/// <summary>
/// Factory for selecting the appropriate battery feature implementation
/// based on what features are supported by a device.
/// </summary>
public static class BatteryFeatureFactory
{
    // Singleton instances of battery feature implementations
    private static readonly Dictionary<ushort, IBatteryFeature> _features = new()
    {
        [HidppFeature.BATTERY_UNIFIED_LEVEL] = new BatteryUnifiedLevel(),
        [HidppFeature.BATTERY_VOLTAGE] = new BatteryVoltage(),
        [HidppFeature.BATTERY_UNIFIED] = new BatteryUnified(),
    };

    /// <summary>
    /// Get the best available battery feature implementation for a device.
    /// </summary>
    /// <param name="featureMap">The device's feature map (feature ID -> feature index mapping)</param>
    /// <returns>
    /// The battery feature implementation, or null if the device doesn't support any battery features.
    /// </returns>
    /// <remarks>
    /// Priority order (from most to least preferred):
    /// 1. Battery Unified Level (0x1000) - Most common, direct percentage
    /// 2. Battery Voltage (0x1001) - Requires voltage lookup table for estimation
    /// 3. Unified Battery (0x1004) - Extended feature with capability info
    /// </remarks>
    public static IBatteryFeature? GetBatteryFeature(Dictionary<ushort, byte> featureMap)
    {
        // Check features in priority order
        foreach (var featureId in new[]
        {
            HidppFeature.BATTERY_UNIFIED_LEVEL,  // 0x1000 - preferred
            HidppFeature.BATTERY_VOLTAGE,        // 0x1001 - fallback
            HidppFeature.BATTERY_UNIFIED         // 0x1004 - alternative
        })
        {
            if (featureMap.ContainsKey(featureId))
            {
                return _features[featureId];
            }
        }

        return null; // No battery feature available
    }
}
