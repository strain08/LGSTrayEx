using LGSTrayPrimitives;
using LGSTrayUI.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using Tommy;

namespace LGSTrayUI.Services;

public class TomlSettingsManager : ISettingsManager
{
    private readonly string _settingsPath;

    public TomlSettingsManager(string? path = null)
    {
        _settingsPath = path ?? Path.Combine(AppContext.BaseDirectory, "appsettings.toml");
    }

    private TomlTable ReadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return new TomlTable();
        }

        using var reader = new StreamReader(File.OpenRead(_settingsPath));
        return TOML.Parse(reader);
    }

    private void WriteSettings(TomlTable table)
    {
        try
        {
            // Ensure all tables use proper section formatting (not inline)
            EnsureProperFormatting(table);

            using var writer = new StreamWriter(_settingsPath);
            table.WriteTo(writer);
            writer.Flush();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError("Failed to write settings to file: " + ex.StackTrace);
        }
    }

    /// <summary>
    /// Recursively ensures all tables in the config use proper section formatting (not inline).
    /// </summary>
    private void EnsureProperFormatting(TomlTable table)
    {
        table.IsInline = false;

        foreach (var key in table.Keys)
        {
            if (table[key] is TomlTable nestedTable)
            {
                EnsureProperFormatting(nestedTable);
            }
        }
    }

    public void Repair()
    {
        try
        {
            // Load user's current config
            var userConfig = ReadSettings();

            // Load default config from embedded resource
            var defaultConfig = ReadDefaultSettings();

            // Remove obsolete keys (keys present in user config but not in default config)
            bool changed = RemoveObsoleteKeys(userConfig, defaultConfig);

            if (changed)
            {
                DiagnosticLogger.Log("Configuration repair: obsolete keys removed.");
                WriteSettings(userConfig);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"Failed to repair configuration: {ex.Message}");
            // Ignore errors during repair - don't throw
        }
    }

    /// <summary>
    /// Recursively removes obsolete keys from target (keys not present in source).
    /// Returns true if any changes were made.
    /// </summary>
    private bool RemoveObsoleteKeys(TomlTable target, TomlTable source)
    {
        bool changed = false;

        // Create a list of keys to remove (can't modify collection while iterating)
        var keysToRemove = new List<string>();

        foreach (var key in target.Keys)
        {
            if (!source.HasKey(key))
            {
                // Key exists in user config but not in default config - it's obsolete
                keysToRemove.Add(key);
            }
            else if (target[key] is TomlTable targetTable && source[key] is TomlTable sourceTable)
            {
                // Both are tables (sections) - recurse
                if (RemoveObsoleteKeys(targetTable, sourceTable))
                {
                    changed = true;
                }
            }
            // If key exists in both and is not a table, keep it (valid user setting)
        }

        // Remove obsolete keys
        foreach (var key in keysToRemove)
        {
            target.Delete(key);
            DiagnosticLogger.Log($"Removed obsolete configuration key: {key}");
            changed = true;
        }

        return changed;
    }

    public void MergeMissingKeys()
    {
        try
        {
            // Load user's current config
            var userConfig = ReadSettings();
            
            // Load default config from embedded resource
            var defaultConfig = ReadDefaultSettings();

            // Merge: add missing keys from defaults to user config
            bool changed = MergeTomlTables(userConfig, defaultConfig);

            if (changed)
            {
                DiagnosticLogger.Log("Configuration updated: missing keys merged from defaults.");
                WriteSettings(userConfig);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"Failed to merge missing configuration keys: {ex.Message}");
            throw;
        }
    }

    private TomlTable ReadDefaultSettings()
    {
        // Read embedded default appsettings.toml from resources
        string defaultToml = System.Text.Encoding.UTF8.GetString(
            LGSTrayUI.Properties.Resources.defaultAppsettings
        );

        using var reader = new StringReader(defaultToml);
        return TOML.Parse(reader);
    }

    /// <summary>
    /// Recursively merges missing keys from source into target.
    /// Returns true if any changes were made.
    /// </summary>
    private bool MergeTomlTables(TomlTable target, TomlTable source)
    {
        bool changed = false;

        foreach (var key in source.Keys)
        {
            if (!target.HasKey(key))
            {
                // Key missing in user config - add from defaults
                if (source[key] is TomlTable sourceTable)
                {
                    // Deep clone the table to avoid inline table formatting
                    target[key] = CloneTomlTable(sourceTable);
                }
                else
                {
                    // For non-table values, direct assignment is fine
                    target[key] = source[key];
                }
                DiagnosticLogger.Log($"Added missing configuration key: {key}");
                changed = true;
            }
            else if (target[key] is TomlTable targetTable && source[key] is TomlTable sourceTable)
            {
                // Both are tables (sections) - recurse
                if (MergeTomlTables(targetTable, sourceTable))
                {
                    changed = true;
                }
            }
            // If key exists and is not a table, keep user's value (don't overwrite)
        }

        return changed;
    }

    /// <summary>
    /// Deep clones a TomlTable to ensure proper section formatting in output.
    /// </summary>
    private TomlTable CloneTomlTable(TomlTable source)
    {
        var cloned = new TomlTable
        {
            IsInline = false  // Explicitly disable inline formatting
        };

        foreach (var key in source.Keys)
        {
            if (source[key] is TomlTable nestedTable)
            {
                // Recursively clone nested tables
                cloned[key] = CloneTomlTable(nestedTable);
            }
            else
            {
                // Copy primitive values directly
                cloned[key] = source[key];
            }
        }

        return cloned;
    }
}