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
/// Tests for LogiDeviceCollection mode switch functionality (Wired/Wireless)
/// </summary>
[Collection("Sequential")]
public class LogiDeviceCollectionModeSwitchTests
{
    private class MockDisposable : IDisposable { public void Dispose() { } }

    private static LogiDeviceCollection CreateTestCollection(
        out Mock<IPublisher<DeviceBatteryUpdatedMessage>> publisherMock,
        out Action<IPCMessage> messageHandler)
    {
        var dispatcher = new SynchronousDispatcher();
        var subscriberMock = new Mock<ISubscriber<IPCMessage>>();
        publisherMock = new Mock<IPublisher<DeviceBatteryUpdatedMessage>>();

        IMessageHandler<IPCMessage> capturedHandler = null!;
        subscriberMock.Setup(s => s.Subscribe(It.IsAny<IMessageHandler<IPCMessage>>(), It.IsAny<MessageHandlerFilter<IPCMessage>[]>()))
            .Callback<IMessageHandler<IPCMessage>, MessageHandlerFilter<IPCMessage>[]>((handler, _) => capturedHandler = handler)
            .Returns(new MockDisposable());

        var settings = new UserSettingsWrapper();
        
        // Ensure clean state
        settings.SelectedSignatures.Clear();
        settings.SelectedDevices.Clear();

        var appSettings = new AppSettings
        {
            UI = new UISettings(),
            Logging = new LoggingSettings { Enabled = false, Verbose = false },
            HTTPServer = new HttpServerSettings { Enabled = false, Addr = "localhost", Port = 12321 },
            GHub = new GHubManagerSettings { Enabled = false },
            Native = new NativeDeviceManagerSettings { Enabled = false },
            Notifications = new NotificationSettings()
        };

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
    public void OnUpdateMessage_WiredMode_UpdatesDeviceAndKeepsOnline()
    {
        // Arrange
        var collection = CreateTestCollection(out var publisherMock, out var messageHandler);
        
        // Add a device first
        string deviceId = "dev001";
        messageHandler(new InitMessage(deviceId, "Test Mouse", true, DeviceType.Mouse));
        var device = collection.Devices.First();
        
        // Send initial update to bring online
        messageHandler(new UpdateMessage(deviceId, 50, PowerSupplyStatus.DISCHARGING, 3900, System.DateTimeOffset.Now));

        // Initial state check
        Assert.False(device.IsWiredMode);
        Assert.True(device.IsOnline);

        // Reset verify invocations
        publisherMock.Invocations.Clear();

        // Act - Simulate Wired Mode update (charging, usually comes with negative percentage if battery status is unavailable/charging)
        // But the key flag is IsWiredMode = true
        var updateTime = System.DateTimeOffset.Now;
        var wiredUpdate = new UpdateMessage(
            deviceId: deviceId,
            batteryPercentage: -1, // Often -1 or charging indication
            powerSupplyStatus: PowerSupplyStatus.CHARGING,
            batteryMVolt: 0,
            updateTime: updateTime,
            isWiredMode: true
        );

        messageHandler(wiredUpdate);

        // Assert
        Assert.True(device.IsWiredMode, "Device should be in wired mode");
        Assert.True(device.IsOnline, "Device should remain online in wired mode"); // It's considered online if we are receiving updates, even if battery is unknown?
        // Actually LogiDeviceCollection.OnUpdateMessage logic says:
        // if (updateMessage.batteryPercentage < 0) { if (updateMessage.IsWiredMode) { ... device.UpdateState ... } else { ... MarkAsOffline ... } }
        // So it should NOT be marked offline (which sets IsOnline = false).
        // Let's verify IsOnline. LogiDeviceViewModel might calculate IsOnline based on BatteryPercentage?
        // Let's check LogiDeviceViewModel or LogiDevice implementation of IsOnline.
        // If IsOnline is manually set to false in MarkAsOffline, then here it should remain true (or whatever UpdateState sets).
        
        // Verify notification
        publisherMock.Verify(p => p.Publish(It.Is<DeviceBatteryUpdatedMessage>(m => m.Device == device)), Times.Once);
    }

    [Fact]
    public void OnUpdateMessage_ReturnToWireless_UpdatesDevice()
    {
        // Arrange
        var collection = CreateTestCollection(out var publisherMock, out var messageHandler);
        string deviceId = "dev001";
        messageHandler(new InitMessage(deviceId, "Test Mouse", true, DeviceType.Mouse));
        var device = collection.Devices.First();

        // Put in wired mode first
        var wiredUpdate = new UpdateMessage(
            deviceId: deviceId,
            batteryPercentage: -1,
            powerSupplyStatus: PowerSupplyStatus.CHARGING,
            batteryMVolt: 0,
            updateTime: System.DateTimeOffset.Now,
            isWiredMode: true
        );
        messageHandler(wiredUpdate);
        Assert.True(device.IsWiredMode);
        
        // Reset invocations to track new ones
        publisherMock.Invocations.Clear();

        // Act - Return to wireless (normal battery update)
        var wirelessUpdate = new UpdateMessage(
            deviceId: deviceId,
            batteryPercentage: 80,
            powerSupplyStatus: PowerSupplyStatus.DISCHARGING,
            batteryMVolt: 3900,
            updateTime: System.DateTimeOffset.Now,
            isWiredMode: false
        );
        messageHandler(wirelessUpdate);

        // Assert
        Assert.False(device.IsWiredMode, "Device should return to wireless mode");
        Assert.Equal(80, device.BatteryPercentage);
        
        // Verify notification
        publisherMock.Verify(p => p.Publish(It.IsAny<DeviceBatteryUpdatedMessage>()), Times.Once);
    }

    [Fact]
    public void OnUpdateMessage_OfflineAndNotWired_MarksOffline()
    {
        // Arrange
        var collection = CreateTestCollection(out var publisherMock, out var messageHandler);
        string deviceId = "dev001";
        messageHandler(new InitMessage(deviceId, "Test Mouse", true, DeviceType.Mouse));
        var device = collection.Devices.First();

        // Act - Simulate Disconnection (Offline, not wired)
        var offlineUpdate = new UpdateMessage(
            deviceId: deviceId,
            batteryPercentage: -1,
            powerSupplyStatus: PowerSupplyStatus.UNKNOWN,
            batteryMVolt: 0,
            updateTime: System.DateTimeOffset.Now,
            isWiredMode: false
        );
        messageHandler(offlineUpdate);

        // Assert
        Assert.False(device.IsWiredMode);
        Assert.False(device.IsOnline, "Device should be marked offline");
    }
}
