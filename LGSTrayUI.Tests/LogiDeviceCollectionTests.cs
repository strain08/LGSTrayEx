using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayUI.Messages;
using LGSTrayUI.Services;
using LGSTrayUI.Tests.Mocks;
using MessagePipe;
using Moq;
using System;
using System.Linq;
using Xunit;

namespace LGSTrayUI.Tests;

/// <summary>
/// Tests for LogiDeviceCollection device removal functionality
/// These tests verify how device removal works to fix duplicate device issue
/// </summary>
[Collection("Sequential")]
public class LogiDeviceCollectionTests
{
    private class MockDisposable : IDisposable { public void Dispose() { } }

    private static LogiDeviceCollection CreateTestCollection(
        out Mock<IPublisher<DeviceBatteryUpdatedMessage>> publisherMock,
        out Action<IPCMessage> messageHandler,
        params string[] initialDeviceSignatures)
    {
        var dispatcher = new SynchronousDispatcher();
        var subscriberMock = new Mock<ISubscriber<IPCMessage>>();
        publisherMock = new Mock<IPublisher<DeviceBatteryUpdatedMessage>>();
        
        IMessageHandler<IPCMessage> capturedHandler = null!;
        subscriberMock.Setup(s => s.Subscribe(It.IsAny<IMessageHandler<IPCMessage>>(), It.IsAny<MessageHandlerFilter<IPCMessage>[]>()))
            .Callback<IMessageHandler<IPCMessage>, MessageHandlerFilter<IPCMessage>[]>((handler, _) => capturedHandler = handler)
            .Returns(new MockDisposable());

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

        // Create mock AppSettings with default values
        var appSettings = new AppSettings
        {
            UI = new UISettings { /* KeepOfflineDevices removed */ },
            Logging = new LoggingSettings { Enabled = false, Verbose = false },
            HTTPServer = new HttpServerSettings { Enabled = false, Addr = "localhost", Port = 12321 },
            GHub = new GHubManagerSettings { Enabled = false },
            Native = new NativeDeviceManagerSettings { Enabled = false },
            Notifications = new NotificationSettings()
        };

        // Use mock icon factory that doesn't create WPF controls (avoids STA thread requirement)
        var iconFactory = new MockLogiDeviceIconFactory();
        var viewModelFactory = new LogiDeviceViewModelFactory(iconFactory, appSettings, settings);

        var republishSubscriberMock = new Mock<ISubscriber<ForceRepublishMessage>>();

        var collection = new LogiDeviceCollection(
            settings,
            viewModelFactory,
            subscriberMock.Object,
            republishSubscriberMock.Object,
            dispatcher,
            publisherMock.Object);
        
        messageHandler = (msg) => capturedHandler?.Handle(msg);
        return collection;
    }

    [Fact]
    public void OnRemoveMessage_MarksDeviceOffline()
    {
        // This test verifies that when a RemoveMessage is received,
        // the device is marked offline but kept in the collection

        // Arrange
        var collection = CreateTestCollection(out _, out var messageHandler);

        // Add device
        messageHandler(new InitMessage("dev001", "Test Device", true, DeviceType.Mouse));
        Assert.Single(collection.Devices);

        // Act
        // Instead of calling OnRemoveMessage directly (which is public but intended for IPC), 
        // we can use the message handler to simulate the full pipeline or call the method directly.
        // The test was calling OnRemoveMessage directly, let's stick to that or use the handler.
        // Using the handler ensures the subscription wiring is correct.
        messageHandler(new RemoveMessage("dev001", "test"));

        // Assert
        Assert.Single(collection.Devices);
        Assert.False(collection.Devices.First().IsOnline);
    }

    [Fact]
    public void OnRemoveMessage_WildcardGHUB_MarksOfflineOnlyGHubDevices()
    {
        // This test verifies that wildcard removal (*GHUB*) marks all GHUB devices offline
        // but leaves native HID devices intact (and online/unchanged)

        // Arrange
        var collection = CreateTestCollection(out _, out var messageHandler);

        messageHandler(new InitMessage("dev001", "GHUB Device 1", true, DeviceType.Mouse));
        messageHandler(new InitMessage("dev002", "GHUB Device 2", true, DeviceType.Keyboard));
        messageHandler(new InitMessage("ABC123", "HID Device", true, DeviceType.Mouse));

        // Set initial battery levels
        messageHandler(new UpdateMessage(deviceId: "dev001", batteryPercentage: 100, powerSupplyStatus: PowerSupplyStatus.DISCHARGING, batteryMVolt: 4000, updateTime: System.DateTimeOffset.Now));
        messageHandler(new UpdateMessage(deviceId: "dev002", batteryPercentage: 100, powerSupplyStatus: PowerSupplyStatus.DISCHARGING, batteryMVolt: 4000, updateTime: System.DateTimeOffset.Now));
        messageHandler(new UpdateMessage(deviceId: "ABC123", batteryPercentage: 100, powerSupplyStatus: PowerSupplyStatus.DISCHARGING, batteryMVolt: 4000, updateTime: System.DateTimeOffset.Now));

        Assert.Equal(3, collection.Devices.Count);

        // Act
        messageHandler(new RemoveMessage("*GHUB*", "rediscover"));

        // Assert
        Assert.Equal(3, collection.Devices.Count); // All kept
        
        var dev1 = collection.Devices.FirstOrDefault(d => d.DeviceId == "dev001");
        var dev2 = collection.Devices.FirstOrDefault(d => d.DeviceId == "dev002");
        var dev3 = collection.Devices.FirstOrDefault(d => d.DeviceId == "ABC123");

        Assert.False(dev1?.IsOnline);
        Assert.Equal(100, dev1?.BatteryPercentage); // Preserved
        
        Assert.False(dev2?.IsOnline);
        Assert.Equal(100, dev2?.BatteryPercentage); // Preserved
        
        Assert.True(dev3?.IsOnline);
        Assert.Equal(100, dev3?.BatteryPercentage); // Native device should be untouched
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
        settings.SelectedSignatures.Add("  "); // empty
        settings.SelectedSignatures.Add("  "); // empty
        settings.SelectedSignatures.Add("NATIVE.ABC123");
        settings.SelectedSignatures.Add(null!); // null

        var dispatcher = new SynchronousDispatcher();
        var subscriberMock = new Mock<ISubscriber<IPCMessage>>();
        subscriberMock.Setup(s => s.Subscribe(It.IsAny<IMessageHandler<IPCMessage>>(), It.IsAny<MessageHandlerFilter<IPCMessage>[]>()))
             .Returns(new MockDisposable());
        
        var publisherMock = new Mock<IPublisher<DeviceBatteryUpdatedMessage>>();
        
        var iconFactory = new MockLogiDeviceIconFactory();
        
        var appSettings = new AppSettings
        {
            UI = new UISettings { /* KeepOfflineDevices removed */ },
            Logging = new LoggingSettings { Enabled = false, Verbose = false },
            HTTPServer = new HttpServerSettings { Enabled = false, Addr = "localhost", Port = 12321 },
            GHub = new GHubManagerSettings { Enabled = false },
            Native = new NativeDeviceManagerSettings { Enabled = false },
            Notifications = new NotificationSettings()
        };
        
        var viewModelFactory = new LogiDeviceViewModelFactory(iconFactory, appSettings, settings);

        var republishSubscriberMock = new Mock<ISubscriber<ForceRepublishMessage>>();

        // Act - creating collection triggers deduplication
        var collection = new LogiDeviceCollection(settings, viewModelFactory, subscriberMock.Object, republishSubscriberMock.Object, dispatcher, publisherMock.Object);

        // Assert - Verify signatures deduplicated (unique signatures only)
        Assert.Equal(2, settings.SelectedSignatures.Count);
        Assert.Contains("GHUB.dev001", settings.SelectedSignatures.Cast<string>());
        Assert.Contains("NATIVE.ABC123", settings.SelectedSignatures.Cast<string>());
    }
}