using LGSTrayHID.Features;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;

namespace LGSTrayHID.Centurion.Features;

/// <summary>
/// BatterySOC (0x0104) battery feature implementation.
/// </summary>
public sealed class CenturionBatterySOC(byte featureIndex, ushort featureId) : ICenturionBatteryFeature
{
    public ushort FeatureId => featureId;
    public string FeatureName => "BatterySOC";
    public byte FeatureIndex => featureIndex;

    /// <summary>
    /// Parse BatterySOC (0x0104) response params.
    /// Byte 0: SOC percentage (0-100)
    /// Byte 1: SOC percentage (duplicate)
    /// Byte 2: Charging status (0=discharging, 1=charging, 2=USB charging, 3=full)
    /// </summary>
    public BatteryUpdateReturn? ParseBatteryParams(byte[] parameters)
    {
        if (parameters.Length < 3)
        {
            DiagnosticLogger.LogWarning($"[Centurion] BatterySOC response too short ({parameters.Length} bytes)");
            return null;
        }

        byte soc = Math.Min((byte)100, parameters[0]);
        var status = parameters[2] switch
        {
            0 => PowerSupplyStatus.DISCHARGING,
            1 or 2 => PowerSupplyStatus.CHARGING,
            3 => PowerSupplyStatus.FULL,
            _ => PowerSupplyStatus.UNKNOWN
        };

        DiagnosticLogger.Log($"[Centurion] Battery: {soc}% status={parameters[2]} → {status}");
        return new BatteryUpdateReturn(soc, status, 0);
    }
}
