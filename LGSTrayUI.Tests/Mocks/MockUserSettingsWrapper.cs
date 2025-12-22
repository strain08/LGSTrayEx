using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Specialized;

namespace LGSTrayUI.Tests.Mocks;

/// <summary>
/// Mock UserSettingsWrapper for testing without actual settings persistence
/// </summary>
public partial class MockUserSettingsWrapper : ObservableObject
{
    private readonly StringCollection _selectedDevices = new();
    public List<string> AddedDevices { get; } = new();
    public List<string> RemovedDevices { get; } = new();

    public StringCollection SelectedDevices => _selectedDevices;

    public bool NumericDisplay { get; set; }

    public void AddDevice(string deviceId)
    {
        AddedDevices.Add(deviceId);
        if (!_selectedDevices.Contains(deviceId))
        {
            _selectedDevices.Add(deviceId);
        }
    }

    public void RemoveDevice(string deviceId)
    {
        RemovedDevices.Add(deviceId);
        _selectedDevices.Remove(deviceId);
    }

    /// <summary>
    /// Initialize the mock with specific device IDs for testing
    /// </summary>
    public void InitializeWith(params string[] deviceIds)
    {
        _selectedDevices.Clear();
        foreach (var id in deviceIds)
        {
            _selectedDevices.Add(id);
        }
    }
}
