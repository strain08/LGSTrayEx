using LGSTrayHID.Features;

namespace LGSTrayHID.Centurion.Features;

public interface ICenturionBatteryFeature
{
    ushort FeatureId { get; }
    string FeatureName { get; }
    byte FeatureIndex { get; }
    BatteryUpdateReturn? ParseBatteryParams(byte[] parameters);
}
