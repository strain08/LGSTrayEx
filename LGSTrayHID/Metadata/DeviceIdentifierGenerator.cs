namespace LGSTrayHID.Metadata;

/// <summary>
/// Generates unique device identifiers based on available device information.
/// </summary>
public static class DeviceIdentifierGenerator
{
    /// <summary>
    /// Generates a unique device identifier using priority order:
    /// 1. Serial number (if available)
    /// 2. Unit ID + Model ID combination (if available)
    /// 3. Hash of device name (fallback)
    /// </summary>
    /// <param name="serialNumber">Device serial number (if Feature 0x0003 supports it)</param>
    /// <param name="unitId">Device unit ID from firmware info</param>
    /// <param name="modelId">Device model ID from firmware info</param>
    /// <param name="deviceName">Device name (used for hash fallback)</param>
    /// <returns>Unique device identifier string</returns>
    public static string GenerateIdentifier(string? serialNumber, string? unitId, string? modelId, string deviceName)
    {
        // Priority 1: Use serial number if available
        if (!string.IsNullOrEmpty(serialNumber))
        {
            return serialNumber;
        }

        // Priority 2: Use UnitID-ModelID combination if available
        if (!string.IsNullOrEmpty(unitId) && !string.IsNullOrEmpty(modelId))
        {
            return $"{unitId}-{modelId}";
        }

        // Priority 3: Fallback to hash of device name
        return $"{deviceName.GetHashCode():X04}";
    }
}
