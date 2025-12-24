using Newtonsoft.Json.Linq;
using LGSTrayPrimitives;

namespace LGSTrayCore.Managers;

/// <summary>
/// Safe JSON field extraction helpers for GHUB WebSocket protocol.
/// Protects against crashes when Logitech changes their API structure.
/// </summary>
internal static class GHubJsonHelpers
{
    /// <summary>
    /// Safely extract a string value from a JObject with a fallback default.
    /// </summary>
    /// <param name="obj">The JObject to extract from</param>
    /// <param name="key">The field name</param>
    /// <param name="defaultValue">Value to return if field is missing or null</param>
    /// <returns>The extracted string or the default value</returns>
    public static string GetStringOrDefault(JObject? obj, string key, string defaultValue = "")
    {
        if (obj == null)
        {
            DiagnosticLogger.LogWarning($"GHubJsonHelpers: Attempted to access '{key}' on null JObject");
            return defaultValue;
        }

        try
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return defaultValue;
            }

            return token.ToString();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"GHubJsonHelpers: Failed to extract string '{key}': {ex.Message}");
            return defaultValue;
        }
    }

    /// <summary>
    /// Safely extract a nested string value (e.g., capabilities.hasBatteryStatus).
    /// </summary>
    /// <param name="obj">The root JObject</param>
    /// <param name="path">Array of keys representing the path (e.g., ["capabilities", "name"])</param>
    /// <param name="defaultValue">Value to return if any part of path is missing</param>
    /// <returns>The extracted string or the default value</returns>
    public static string GetNestedStringOrDefault(JObject? obj, string[] path, string defaultValue = "")
    {
        if (obj == null || path == null || path.Length == 0)
        {
            return defaultValue;
        }

        try
        {
            JToken? current = obj;

            foreach (var key in path)
            {
                if (current == null || current.Type == JTokenType.Null)
                {
                    return defaultValue;
                }

                if (current is JObject jObj)
                {
                    current = jObj[key];
                }
                else
                {
                    DiagnosticLogger.LogWarning($"GHubJsonHelpers: Expected JObject at path '{string.Join(".", path)}' but got {current.Type}");
                    return defaultValue;
                }
            }

            return current?.ToString() ?? defaultValue;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"GHubJsonHelpers: Failed to extract nested string at path '{string.Join(".", path)}': {ex.Message}");
            return defaultValue;
        }
    }

    /// <summary>
    /// Safely extract an integer value from a JObject.
    /// </summary>
    /// <param name="obj">The JObject to extract from</param>
    /// <param name="key">The field name</param>
    /// <returns>The extracted integer, or null if field is missing or not an integer</returns>
    public static int? GetInt(JObject? obj, string key)
    {
        if (obj == null)
        {
            return null;
        }

        try
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.ToObject<int>();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"GHubJsonHelpers: Failed to extract int '{key}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Safely extract a double value from a JObject.
    /// </summary>
    /// <param name="obj">The JObject to extract from</param>
    /// <param name="key">The field name</param>
    /// <returns>The extracted double, or null if field is missing or not a double</returns>
    public static double? GetDouble(JObject? obj, string key)
    {
        if (obj == null)
        {
            return null;
        }

        try
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.ToObject<double>();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"GHubJsonHelpers: Failed to extract double '{key}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Safely extract a boolean value from a JObject.
    /// </summary>
    /// <param name="obj">The JObject to extract from</param>
    /// <param name="key">The field name</param>
    /// <returns>The extracted boolean, or null if field is missing or not a boolean</returns>
    public static bool? GetBool(JObject? obj, string key)
    {
        if (obj == null)
        {
            return null;
        }

        try
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.ToObject<bool>();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"GHubJsonHelpers: Failed to extract bool '{key}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Safely extract a nested boolean value (e.g., capabilities.hasBatteryStatus).
    /// </summary>
    /// <param name="obj">The root JObject</param>
    /// <param name="path">Array of keys representing the path (e.g., ["capabilities", "hasBatteryStatus"])</param>
    /// <param name="defaultValue">Value to return if any part of path is missing or not a boolean</param>
    /// <returns>The extracted boolean or the default value</returns>
    public static bool GetNestedBoolOrDefault(JObject? obj, string[] path, bool defaultValue = false)
    {
        if (obj == null || path == null || path.Length == 0)
        {
            return defaultValue;
        }

        try
        {
            JToken? current = obj;

            // Navigate to the parent of the final key
            for (int i = 0; i < path.Length - 1; i++)
            {
                if (current == null || current.Type == JTokenType.Null)
                {
                    return defaultValue;
                }

                if (current is JObject jObj)
                {
                    current = jObj[path[i]];
                }
                else
                {
                    DiagnosticLogger.LogWarning($"GHubJsonHelpers: Expected JObject at path '{string.Join(".", path)}' but got {current.Type}");
                    return defaultValue;
                }
            }

            // Extract final value
            if (current is JObject finalObj)
            {
                var finalToken = finalObj[path[^1]];
                if (finalToken == null || finalToken.Type == JTokenType.Null)
                {
                    return defaultValue;
                }

                return finalToken.ToObject<bool>();
            }

            return defaultValue;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"GHubJsonHelpers: Failed to extract nested bool at path '{string.Join(".", path)}': {ex.Message}");
            return defaultValue;
        }
    }

    /// <summary>
    /// Validate that a JObject contains all required fields.
    /// </summary>
    /// <param name="obj">The JObject to validate</param>
    /// <param name="requiredFields">Array of field names that must exist</param>
    /// <returns>True if all fields exist and are not null, false otherwise</returns>
    public static bool HasRequiredFields(JObject? obj, params string[] requiredFields)
    {
        if (obj == null)
        {
            DiagnosticLogger.LogWarning("GHubJsonHelpers: HasRequiredFields called with null JObject");
            return false;
        }

        if (requiredFields == null || requiredFields.Length == 0)
        {
            return true;
        }

        foreach (var field in requiredFields)
        {
            var token = obj[field];
            if (token == null || token.Type == JTokenType.Null)
            {
                DiagnosticLogger.LogWarning($"GHubJsonHelpers: Required field '{field}' is missing or null");
                return false;
            }
        }

        return true;
    }
}
