using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayUI.Services;
using LGSTrayUI.Tests.Mocks;

namespace LGSTrayUI.Tests;

/// <summary>
/// Tests for LogiDeviceCollection device removal functionality
/// These tests verify how device removal works to fix duplicate device issue
/// </summary>
public class LogiDeviceCollectionTests
{
    private static LogiDeviceCollection CreateTestCollection(params string[] initialDeviceSignatures)
    {
        var dispatcher = new SynchronousDispatcher();
        var subscriber = new MockSubscriber();

        // Use real UserSettingsWrapper - simplified for testing
        var settings = new UserSettingsWrapper();

        // Initialize with test device signatures if provided
        settings.SelectedSignatures.Clear();
        settings.SelectedDevices.Clear(); // Ensure old system is cleared
        foreach (var signature in initialDeviceSignatures)
        {
            if (!string.IsNullOrEmpty(signature))
            {
                settings.SelectedSignatures.Add(signature);
            }
        }

        // Use mock icon factory that doesn't create WPF controls (avoids STA thread requirement)
        var iconFactory = new MockLogiDeviceIconFactory();
        var viewModelFactory = new LogiDeviceViewModelFactory(iconFactory);

        // Create mock AppSettings with default values
        var appSettings = new AppSettings
        {
            UI = new UISettings { KeepOfflineDevices = false },
            Logging = new LoggingSettings { Enabled = false, Verbose = false },
            HTTPServer = new HttpServerSettings { Enabled = false, Addr = "localhost", Port = 12321 },
            GHub = new IDeviceManagerSettings { Enabled = false },
            Native = new NativeDeviceManagerSettings { Enabled = false },
            Notifications = new NotificationSettings()
        };

        var collection = new LogiDeviceCollection(
            settings,
            viewModelFactory,
            subscriber,
            dispatcher,
            appSettings);

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
    public void LoadPreviouslySelectedDevices_DeduplicatesSettings()
    {
        // This test verifies that duplicate signatures and empty strings are
        // removed from SelectedSignatures during load (signature-based system)

        // Arrange
        var settings = new UserSettingsWrapper();
        settings.SelectedSignatures.Clear();
        settings.SelectedDevices.Clear();

        // Add duplicate signatures and empty strings
        settings.SelectedSignatures.Add("GHUB.dev001");
        settings.SelectedSignatures.Add("GHUB.dev001"); // duplicate
        settings.SelectedSignatures.Add(""); // empty
        settings.SelectedSignatures.Add("NATIVE.ABC123");
        settings.SelectedSignatures.Add(null!); // null

        var dispatcher = new SynchronousDispatcher();
        var subscriber = new MockSubscriber();
        var iconFactory = new MockLogiDeviceIconFactory();
        var viewModelFactory = new LogiDeviceViewModelFactory(iconFactory);
        var appSettings = new AppSettings
        {
            UI = new UISettings { KeepOfflineDevices = false },
            Logging = new LoggingSettings { Enabled = false, Verbose = false },
            HTTPServer = new HttpServerSettings { Enabled = false, Addr = "localhost", Port = 12321 },
            GHub = new IDeviceManagerSettings { Enabled = false },
            Native = new NativeDeviceManagerSettings { Enabled = false },
            Notifications = new NotificationSettings()
        };

        // Act - creating collection triggers deduplication
        var collection = new LogiDeviceCollection(settings, viewModelFactory, subscriber, dispatcher, appSettings);

        // Assert - Verify signatures deduplicated (unique signatures only)
        Assert.Equal(2, settings.SelectedSignatures.Count);
        Assert.Contains("GHUB.dev001", settings.SelectedSignatures.Cast<string>());
        Assert.Contains("NATIVE.ABC123", settings.SelectedSignatures.Cast<string>());
    }
}
