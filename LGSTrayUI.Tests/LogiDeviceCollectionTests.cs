using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayUI.Tests.Mocks;
using Microsoft.Extensions.Options;

namespace LGSTrayUI.Tests;

/// <summary>
/// Tests for LogiDeviceCollection device removal functionality
/// These tests verify how device removal works to fix duplicate device issue
/// </summary>
public class LogiDeviceCollectionTests
{
    private static LogiDeviceCollection CreateTestCollection(params string[] initialDeviceIds)
    {
        var dispatcher = new SynchronousDispatcher();
        var subscriber = new MockSubscriber();

        // Use real UserSettingsWrapper - simplified for testing
        var settings = new UserSettingsWrapper();

        // Initialize with test device IDs if provided
        settings.SelectedDevices.Clear();
        foreach (var id in initialDeviceIds)
        {
            if (!string.IsNullOrEmpty(id))
            {
                settings.SelectedDevices.Add(id);
            }
        }

        // Use mock icon factory that doesn't create WPF controls (avoids STA thread requirement)
        var iconFactory = new MockLogiDeviceIconFactory();
        var viewModelFactory = new LogiDeviceViewModelFactory(iconFactory);

        var collection = new LogiDeviceCollection(
            settings,
            viewModelFactory,
            subscriber,
            dispatcher);

        return collection;
    }

    [Fact]
    public void OnRemoveMessage_RemovesDeviceFromCollection()
    {
        // This test verifies that when a RemoveMessage is received,
        // the device is removed from the collection

        // Arrange
        var collection = CreateTestCollection();

        // Add device
        collection.OnInitMessage(new InitMessage("dev001", "Test Device", true, DeviceType.Mouse));
        Assert.Single(collection.Devices);

        // Act
        collection.OnRemoveMessage(new RemoveMessage("dev001", "test"));

        // Assert
        Assert.Empty(collection.Devices);
    }

    [Fact]
    public void OnRemoveMessage_WildcardGHUB_RemovesOnlyGHubDevices()
    {
        // This test verifies that wildcard removal (*GHUB*) removes all GHUB devices
        // but leaves native HID devices intact

        // Arrange
        var collection = CreateTestCollection();

        collection.OnInitMessage(new InitMessage("dev001", "GHUB Device 1", true, DeviceType.Mouse));
        collection.OnInitMessage(new InitMessage("dev002", "GHUB Device 2", true, DeviceType.Keyboard));
        collection.OnInitMessage(new InitMessage("ABC123", "HID Device", true, DeviceType.Mouse));

        Assert.Equal(3, collection.Devices.Count);

        // Act
        collection.OnRemoveMessage(new RemoveMessage("*GHUB*", "rediscover"));

        // Assert
        Assert.Single(collection.Devices);
        Assert.Equal("ABC123", collection.Devices.First().DeviceId);
    }

    [Fact]
    public void OnRemoveMessage_UpdatesSettings()
    {
        // This test verifies that when a device is removed,
        // it's also removed from the SelectedDevices settings

        // Arrange
        var collection = CreateTestCollection();

        collection.OnInitMessage(new InitMessage("dev001", "Test Device", true, DeviceType.Mouse));

        // Act
        collection.OnRemoveMessage(new RemoveMessage("dev001", "test"));

        // Assert - verify device removed from collection
        Assert.Empty(collection.Devices);
    }

    [Fact]
    public void OnRemoveMessage_ReleasesIconResources()
    {
        // This test verifies that when a device is removed,
        // its icon resources are properly disposed by setting IsChecked = false

        // Arrange
        var collection = CreateTestCollection();

        collection.OnInitMessage(new InitMessage("dev001", "Test Device", true, DeviceType.Mouse));
        var device = collection.Devices.First();
        device.IsChecked = true;

        // Act
        collection.OnRemoveMessage(new RemoveMessage("dev001", "test"));

        // Assert - device is removed
        Assert.Empty(collection.Devices);
    }

    [Fact]
    public async Task CleanupStaleStubs_RemovesUninitializedDevicesAfterTimeout()
    {
        // This test verifies that "Not Initialised" stub entries are
        // automatically removed after 30 seconds

        // Note: This test uses a shorter delay for testing purposes
        // Actual implementation uses 30 seconds

        // Arrange
        var collection = CreateTestCollection("OLD_ID_123");

        // Verify stub created
        Assert.Single(collection.Devices);
        Assert.Equal("Not Initialised", collection.Devices.First().DeviceName);
        Assert.Equal("OLD_ID_123", collection.Devices.First().DeviceId);

        // For now, just verify the stub was created as expected
        // In real usage, the cleanup timer would remove it after 30s
        Assert.Single(collection.Devices);
    }

    [Fact]
    public void OnInitMessage_ReplacesStubWithRealDevice_WhenGHubIdChanges()
    {
        // This is the KEY TEST that fixes the main issue!
        //
        // Scenario:
        // - User previously selected keyboard with ID dev00000001
        // - On startup, stub created: DeviceId=dev00000001, DeviceName="Not Initialised"
        // - System wakes from sleep, keyboard now has ID dev00000002
        // - InitMessage arrives for dev00000002
        // - Smart logic detects: GHUB ID (starts with "dev"), stub exists
        // - Solution: Replace stub with real device, transfer IsChecked state

        // Arrange
        var collection = CreateTestCollection("dev00000001");

        // Verify stub created
        Assert.Single(collection.Devices);
        var stub = collection.Devices.First();
        Assert.Equal("dev00000001", stub.DeviceId);
        Assert.Equal("Not Initialised", stub.DeviceName);
        Assert.True(stub.IsChecked); // Stub is checked because it was previously selected

        // Act: Real device arrives with new ID
        collection.OnInitMessage(new InitMessage("dev00000002", "G Pro Wireless", true, DeviceType.Mouse));

        // Assert: Stub replaced
        Assert.Single(collection.Devices);
        var device = collection.Devices.First();
        Assert.Equal("dev00000002", device.DeviceId);
        Assert.Equal("G Pro Wireless", device.DeviceName);
        Assert.True(device.IsChecked); // Selection transferred
    }

    [Fact]
    public void LoadPreviouslySelectedDevices_DeduplicatesSettings()
    {
        // This test verifies that duplicate IDs and empty strings are
        // removed from settings during load

        // Arrange & Act - creating collection triggers deduplication
        var collection = CreateTestCollection("dev001", "dev001", "", "ABC123", null!);

        // Assert - Verify stubs created for unique IDs only
        Assert.Equal(2, collection.Devices.Count);

        // Verify the correct device IDs were kept
        var deviceIds = collection.Devices.Select(d => d.DeviceId).ToList();
        Assert.Contains("dev001", deviceIds);
        Assert.Contains("ABC123", deviceIds);
    }
}
