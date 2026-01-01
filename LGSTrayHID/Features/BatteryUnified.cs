using LGSTrayHID.Protocol;
using LGSTrayPrimitives;

namespace LGSTrayHID.Features;

/// <summary>
/// Unified Battery (Feature 0x1004).
/// Extended battery feature that provides battery capability information.
/// Uses a different function ID (0x10) compared to 0x1000.
/// </summary>
public class BatteryUnified : IBatteryFeature
{
    /// <inheritdoc/>
    public ushort FeatureId => HidppFeature.BATTERY_UNIFIED;

    public string FeatureName => "Unified Battery";

    /// <inheritdoc/>
    public async Task<BatteryUpdateReturn?> GetBatteryAsync(HidppDevice device)
    {
        // Build command using the new builder pattern
        // Note: This feature uses function 0x10 (GET_CAPABILITY) instead of 0x00 (GET_STATUS)
        var command = Hidpp20CommandBuilder.Create(device.DeviceIdx)
            .WithFeatureIndex(device.FeatureMap[FeatureId])
            .WithFunction(BatteryFunction.GET_CAPABILITY)
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
        
        // Feature 0x1004 doesn't provide voltage info
        int millivolts = -1;

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

        // Event payload format for Feature 0x1004:
        // Note: 0x1004 events use function 0x00 (broadcast) vs 0x10 for queries
        // but payload format matches capability response
        // Param 0: Battery percentage (0-100)
        // Param 1: Battery level flags (validate before using)
        // Param 2: Charging status code
        double percentage = eventMessage.GetParam(0);
        byte levelFlags = eventMessage.GetParam(1);

        if (!BatteryStatusParser.IsValidBatteryLevelFlags(levelFlags))
        {
            DiagnosticLogger.Log($"[Feature {FeatureId}] Invalid battery level flags in event: 0x{levelFlags:X2} (multiple or no flags set). Rejecting corrupt data.");
            return null;
        }
        var status = BatteryStatusParser.ParseUnifiedBatteryStatus(eventMessage.GetParam(2));

        // Feature 0x1004 doesn't provide voltage info
        int millivolts = -1;

        return new BatteryUpdateReturn(percentage, status, millivolts);
    }
}
