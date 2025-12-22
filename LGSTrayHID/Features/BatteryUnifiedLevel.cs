using LGSTrayPrimitives;
using LGSTrayHID.Protocol;

namespace LGSTrayHID.Features;

/// <summary>
/// Battery Unified Level Status (Feature 0x1000).
/// This feature provides battery percentage and charging status directly from the device.
/// Most common battery feature on Logitech devices.
/// </summary>
public class BatteryUnifiedLevel : IBatteryFeature
{
    private const int QueryTimeout = 5000;
    /// <inheritdoc/>
    public ushort FeatureId => HidppFeature.BATTERY_UNIFIED_LEVEL;

    public string FeatureName => "Battery Unified Level";

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
        // Param 0: Battery percentage (0-100)
        // Param 2: Charging status code
        double percentage = response.GetParam(0);
        var status = BatteryStatusParser.ParseUnifiedBatteryStatus(response.GetParam(2));

        // Feature 0x1000 doesn't provide voltage info
        int millivolts = -1;

        return new BatteryUpdateReturn(percentage, status, millivolts);
    }

    /// <inheritdoc/>
    public BatteryUpdateReturn? ParseBatteryEvent(Hidpp20 eventMessage)
    {
        // Validate this is an event for our feature
        // Events have function index 0x00
        if (eventMessage.GetFunctionId() != Protocol.BatteryEventFunction.BATTERY_STATUS_BROADCAST)
        {
            return null;
        }

        // Event payload format matches query response format for Feature 0x1000:
        // Param 0: Battery percentage (0-100)
        // Param 2: Charging status code
        double percentage = eventMessage.GetParam(0);
        var status = BatteryStatusParser.ParseUnifiedBatteryStatus(eventMessage.GetParam(2));

        // Feature 0x1000 doesn't provide voltage info
        int millivolts = -1;

        return new BatteryUpdateReturn(percentage, status, millivolts);
    }
}
