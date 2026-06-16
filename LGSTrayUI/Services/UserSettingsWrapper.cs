using CommunityToolkit.Mvvm.ComponentModel;
using LGSTrayPrimitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LGSTrayUI.Services;

/// <summary>
/// Persists per-user UI settings (selected device signatures, numeric display, keep-offline)
/// to a single JSON file in <see cref="AppDataPaths.LocalAppDataDir"/>.
///
/// Replaces the legacy .NET <c>Properties.Settings</c> (<c>user.config</c>) store, which spawned
/// a new per-version / per-install-path folder under <c>%LOCALAPPDATA%\LGSTray\</c> that was
/// never cleaned up. On first run this class imports any existing legacy values and then deletes
/// the orphaned <c>LGSTray_*</c> folders.
/// </summary>
public partial class UserSettingsWrapper : ObservableObject
{
    private sealed class UserSettingsData
    {
        public List<string> SelectedSignatures { get; set; } = [];
        public bool NumericDisplay { get; set; }
        public bool KeepOfflineDevices { get; set; }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly object _ioLock = new();

    private readonly string _filePath;
    private readonly UserSettingsData _data;

    public UserSettingsWrapper()
        : this(Path.Combine(AppDataPaths.LocalAppDataDir, "usersettings.json"), migrateLegacy: true)
    {
    }

    private UserSettingsWrapper(string filePath, bool migrateLegacy)
    {
        _filePath = filePath;
        _data = Load(filePath, migrateLegacy);
    }

    /// <summary>
    /// Creates an isolated instance backed by a throwaway temp file with no legacy import or
    /// cleanup. For tests only — never touches the real user-settings file or legacy folders.
    /// </summary>
    public static UserSettingsWrapper CreateEphemeral()
        => new(Path.Combine(Path.GetTempPath(), $"lgstray_test_{Guid.NewGuid():N}.json"), migrateLegacy: false);

    public IReadOnlyList<string> SelectedSignatures => _data.SelectedSignatures;

    public bool NumericDisplay
    {
        get => _data.NumericDisplay;
        set
        {
            if (_data.NumericDisplay == value) return;
            _data.NumericDisplay = value;
            Save();
            OnPropertyChanged();
        }
    }

    public bool KeepOfflineDevices
    {
        get => _data.KeepOfflineDevices;
        set
        {
            if (_data.KeepOfflineDevices == value) return;
            _data.KeepOfflineDevices = value;
            Save();
            OnPropertyChanged();
        }
    }

    public void AddSignature(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return;
        if (_data.SelectedSignatures.Contains(signature)) return;

        _data.SelectedSignatures.Add(signature);
        Save();
        OnPropertyChanged(nameof(SelectedSignatures));
    }

    public void RemoveSignature(string signature)
    {
        if (!_data.SelectedSignatures.Remove(signature)) return;

        Save();
        OnPropertyChanged(nameof(SelectedSignatures));
    }

    public bool ContainsSignature(string signature)
        => _data.SelectedSignatures.Contains(signature);

    private void Save()
    {
        lock (_ioLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, _jsonOptions));
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError($"Failed to write user settings to '{_filePath}': {ex.Message}");
            }
        }
    }

    private static UserSettingsData Load(string filePath, bool migrateLegacy)
    {
        lock (_ioLock)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var data = JsonSerializer.Deserialize<UserSettingsData>(File.ReadAllText(filePath));
                    if (data != null)
                    {
                        data.SelectedSignatures = Dedupe(data.SelectedSignatures);
                        return data;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError($"Failed to read user settings from '{filePath}': {ex.Message}. Starting fresh.");
            }

            if (!migrateLegacy)
            {
                return new UserSettingsData();
            }

            // First run on the JSON-backed store: pull existing values out of the legacy
            // Properties.Settings store, persist them as JSON, then remove the orphaned folders.
            var imported = ImportLegacySettings();
            TrySave(filePath, imported);
            CleanupLegacySettingsFolders();
            return imported;
        }
    }

    private static UserSettingsData ImportLegacySettings()
    {
        var data = new UserSettingsData();
        try
        {
            var legacy = Properties.Settings.Default;

            // The app historically never called Upgrade(), so a freshly-installed version starts
            // from defaults. Upgrade() pulls the most recent previous version's values (for the
            // same install path) into memory without writing a new user.config folder.
            try { legacy.Upgrade(); }
            catch (Exception ex) { DiagnosticLogger.LogWarning($"Legacy settings Upgrade() failed: {ex.Message}"); }

            data.NumericDisplay = legacy.NumericDisplay;
            data.KeepOfflineDevices = legacy.KeepOfflineDevices;
            if (legacy.SelectedSignatures != null)
            {
                data.SelectedSignatures = Dedupe(legacy.SelectedSignatures.Cast<string>());
            }

            DiagnosticLogger.Log(
                $"Migrated legacy user settings to JSON: {data.SelectedSignatures.Count} signature(s), " +
                $"NumericDisplay={data.NumericDisplay}, KeepOfflineDevices={data.KeepOfflineDevices}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"Failed to import legacy user settings: {ex.Message}");
        }
        return data;
    }

    /// <summary>
    /// Deletes the legacy <c>LGSTray_*</c> <c>user.config</c> folders left behind by the old
    /// Properties.Settings store. Best-effort — failures are logged, not fatal.
    /// </summary>
    private static void CleanupLegacySettingsFolders()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(AppDataPaths.LocalAppDataDir, "LGSTray_*"))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    DiagnosticLogger.Log($"Removed legacy settings folder '{Path.GetFileName(dir)}'");
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogWarning($"Could not remove legacy settings folder '{dir}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"Legacy settings cleanup skipped: {ex.Message}");
        }
    }

    private static void TrySave(string filePath, UserSettingsData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(data, _jsonOptions));
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"Failed to write user settings to '{filePath}': {ex.Message}");
        }
    }

    private static List<string> Dedupe(IEnumerable<string> signatures)
        => signatures.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
}
