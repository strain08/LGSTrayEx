using LGSTrayPrimitives;

namespace LGSTrayHID.Features
{
    /// <summary>
    /// Common interface for all HID++ battery features.
    /// Different battery features (0x1000, 0x1001, 0x1004) provide battery status
    /// using different protocols and data formats, but all implement this interface
    /// to provide a unified query mechanism.
    /// </summary>
    public interface IBatteryFeature
    {
        /// <summary>
        /// The HID++ feature ID for this battery feature.
        /// </summary>
        /// <value>
        /// - 0x1000: Battery Unified Level Status
        /// - 0x1001: Battery Voltage (with lookup table estimation)
        /// - 0x1004: Unified Battery (extended)
        /// </value>
        ushort FeatureId { get; }
        string FeatureName { get; }
        /// <summary>
        /// Query battery status from the device using this feature.
        /// </summary>
        /// <param name="device">The HID++ device to query</param>
        /// <returns>Battery status information, or null if the query failed</returns>
        Task<BatteryUpdateReturn?> GetBatteryAsync(HidppDevice device);
    }
}
