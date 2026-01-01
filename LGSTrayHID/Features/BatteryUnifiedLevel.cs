using LGSTrayHID.Protocol;
using LGSTrayPrimitives;

namespace LGSTrayHID.Features;

/// <summary>
/// Battery Unified Level Status (Feature 0x1000).
/// This feature provides battery percentage and charging status directly from the device.
/// Most common battery feature on Logitech devices.
/// </summary>
public class BatteryUnifiedLevel : IBatteryFeature
{
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
        // Param 0: Battery percentage (0-100)
        // Param 1: Battery level flags (validate before using)
        // Param 2: Charging status code
        double percentage = response.GetParam(0);
        byte levelFlags = response.GetParam(1);

        if (!BatteryStatusParser.IsValidBatteryLevelFlags(levelFlags))
        {
            DiagnosticLogger.Log($"[Feature {FeatureId}] Invalid battery level flags: 0x{levelFlags:X2} (multiple or no flags set). Rejecting corrupt data.");
            return null;
        }

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
        
        double percentage = eventMessage.GetParam(0);
        byte levelFlags = eventMessage.GetParam(1);

        // Validate battery level flags before accepting event
        if (!BatteryStatusParser.IsValidBatteryLevelFlags(levelFlags))
        {
            DiagnosticLogger.Log($"[Feature 0x1004 Event] Invalid battery level flags: 0x{levelFlags:X2}. Rejecting corrupt event.");
            return null;
        }

        var status = BatteryStatusParser.ParseUnifiedBatteryStatus(eventMessage.GetParam(2));

        // Feature 0x1000 doesn't provide voltage info
        int millivolts = -1;

        return new BatteryUpdateReturn(percentage, status, millivolts);
    }
}
