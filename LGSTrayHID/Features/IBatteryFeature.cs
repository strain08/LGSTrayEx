using LGSTrayHID.Protocol;

namespace LGSTrayHID.Features;

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
    /// Query battery status from the device using this feature (request/response).
    /// </summary>
    /// <param name="device">The HID++ device to query</param>
    /// <returns>Battery status information, or null if the query failed</returns>
    Task<BatteryUpdateReturn?> GetBatteryAsync(HidppDevice device);

    /// <summary>
    /// Parse a battery event broadcast message from the device.
    /// Battery events are unsolicited messages sent when battery state changes.
    /// </summary>
    /// <param name="eventMessage">The HID++ 2.0 event message</param>
    /// <returns>Battery status information, or null if parsing failed</returns>
    /// <remarks>
    /// Event messages have function index 0x00 and are distinguished from query
    /// responses by the calling context (not matched to a request).
    /// Event payload format is feature-specific but typically matches query response format.
    /// </remarks>
    BatteryUpdateReturn? ParseBatteryEvent(Hidpp20 eventMessage);

    /// <summary>
    /// Determine whether a battery event broadcast signals that the device has gone
    /// offline / inactive (e.g. a headset powering off or entering deep sleep) rather
    /// than simply carrying an unparseable/garbage measurement.
    /// </summary>
    /// <param name="eventMessage">The HID++ 2.0 event message that failed to parse into a battery update.</param>
    /// <returns>True if the event indicates the device became inactive and should be marked offline.</returns>
    /// <remarks>
    /// Defaults to false: most receiver-attached features (0x1000/0x1001/0x1004) signal
    /// disconnection through the receiver's device OFF announcement (0x41), not the battery
    /// feature itself. Direct-connect features such as 0x1F20 (ADC measurement) override this
    /// because the same feature both reports voltage and, with the valid bit cleared, announces
    /// the device went inactive (matching Solaar's notifications.py ADC_MEASUREMENT handling).
    /// </remarks>
    bool IsOfflineEvent(Hidpp20 eventMessage) => false;
}
