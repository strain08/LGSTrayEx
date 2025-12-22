namespace LGSTrayHID.Metadata;

/// <summary>
/// Validates devices against configuration rules to determine if they should be allowed.
/// </summary>
public static class DeviceFilterValidator
{
    /// <summary>
    /// Checks if a device is allowed based on disabled device patterns.
    /// </summary>
    /// <param name="deviceName">The device name to check</param>
    /// <param name="disabledPatterns">List of device name patterns to filter out</param>
    /// <param name="matchedPattern">The pattern that matched, if device is filtered</param>
    /// <returns>True if device is allowed, false if filtered</returns>
    public static bool IsDeviceAllowed(string deviceName, IEnumerable<string> disabledPatterns, out string? matchedPattern)
    {
        foreach (var pattern in disabledPatterns)
        {
            if (deviceName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                matchedPattern = pattern;
                return false;
            }
        }

        matchedPattern = null;
        return true;
    }
}
