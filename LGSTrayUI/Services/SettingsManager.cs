using LGSTrayPrimitives;
using System;
using System.IO;
using Tommy;

namespace LGSTrayUI.Services;

public interface ISettingsManager
{
    void Repair();
}

public class SettingsManager : ISettingsManager
{
    private readonly string _settingsPath;

    public SettingsManager(string? path = null)
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
            using var writer = new StreamWriter(_settingsPath);
            table.WriteTo(writer);
            writer.Flush();
        }
        catch (Exception ex) 
        {
            DiagnosticLogger.LogError("Failed to write settings to file: " + ex.StackTrace);
        }
    }

    public void Repair()
    {
        try
        {
            var table = ReadSettings();
            bool changed = false;

            if (table.HasKey("UI"))
            {
                string[] obsoleteKeys = ["KeepOfflineDevices", "keepOfflineDevices"];
                // Remove legacy KeepOfflineDevices key if present (moved to user settings)
                foreach (string key in obsoleteKeys)
                {
                    if (table["UI"].HasKey(key))
                    {
                        table["UI"].Delete(key);
                        changed = true;
                    }
                }
                
            }

            if (changed)
            {
                WriteSettings(table);
            }
        }
        catch
        {
            // Ignore errors during repair
        }
    }
}