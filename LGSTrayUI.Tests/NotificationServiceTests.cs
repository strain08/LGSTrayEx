using CommunityToolkit.Mvvm.Messaging;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayUI.Interfaces;
using LGSTrayUI.Messages;
using LGSTrayUI.Services;
using Microsoft.Extensions.Options;
using Moq;
using Notification.Wpf;

namespace LGSTrayUI.Tests;

public class NotificationServiceTests
{
    private async Task<Mock<INotificationManager>> MockManager(int percentage, AppSettings settingsMock, PowerSupplyStatus powerSupply) 
    {
        var manager = new Mock<INotificationManager>(MockBehavior.Loose);
        var settings = new Mock<IOptions<AppSettings>>();
        var messenger = new StrongReferenceMessenger();
        var iconFactory = new Mock<ILogiDeviceIconFactory>();
        var appSettings = settingsMock;

        settings.Setup(s => s.Value).Returns(appSettings);

        var service = new NotificationService(manager.Object, settings.Object, messenger);
        await service.StartAsync(CancellationToken.None);

        var device = new LogiDeviceViewModel(iconFactory.Object);
        device.UpdateState(new InitMessage(
            deviceId: "test-device-001",
            deviceName: "Test Device",
            hasBattery: true,
            deviceType: DeviceType.Mouse
        ));

        // First update: not charging (to establish baseline - wasn't charging before)
        device.UpdateState(new UpdateMessage(
            deviceId: "test-device-001",
            batteryPercentage: 50,
            powerSupplyStatus: PowerSupplyStatus.POWER_SUPPLY_STATUS_NOT_CHARGING,
            batteryMVolt: 4000,
            updateTime: DateTimeOffset.Now
        ));
        messenger.Send(new DeviceBatteryUpdatedMessage(device));

        // Second update: charging and full (this should trigger notification)
        device.UpdateState(new UpdateMessage(
            deviceId: "test-device-001",
            batteryPercentage: percentage,
            powerSupplyStatus: powerSupply,
            batteryMVolt: 4200,
            updateTime: DateTimeOffset.Now
        ));
        messenger.Send(new DeviceBatteryUpdatedMessage(device));

        // Third update: for duplicate notification prevention (should not trigger notification)
        device.UpdateState(new UpdateMessage(
            deviceId: "test-device-001",
            batteryPercentage: percentage+1,
            powerSupplyStatus: powerSupply,
            batteryMVolt: 4200,
            updateTime: DateTimeOffset.Now
        ));
        messenger.Send(new DeviceBatteryUpdatedMessage(device));

        return manager;
    }

    [Fact]
    public async Task ShouldNotify_WhenBatteryFull()
    {
        // Arrange
        
        var appSettings = new AppSettings
        {
            Notifications = new NotificationSettings
            {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 90
            }
        };

        // Act

        var manager = await MockManager(100, appSettings, PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING);

        // Assert

        Assert.Single(manager.Invocations); // a single notification should be shown
        var invocation = manager.Invocations[0];
        var title = invocation.Arguments[0];
        var msg = invocation.Arguments[1];
        var type = (NotificationType)invocation.Arguments[2];
        
        Assert.Equal("Show", invocation.Method.Name);
        Assert.Equal("Test Device - Battery Full", title);
        Assert.Equal("Battery level: 100%", msg);
        Assert.Equal(NotificationType.Success, type);
    }

    [Fact]
    public async Task ShouldNotify_WhenHighTreshold()
    {
        // Arrange
        
        var appSettings = new AppSettings
        {
            Notifications = new NotificationSettings
            {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 80
            }
        };       
        var batteryPercent = 81;
        
        // Act
        
        var manager = await MockManager(batteryPercent, appSettings, PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING);
        
        // Assert

        Assert.Single(manager.Invocations); // a single notification should be shown
        var invocation = manager.Invocations[0];
        var title = invocation.Arguments[0];
        var msg = invocation.Arguments[1];
        var type = (NotificationType)invocation.Arguments[2];

        Assert.Equal("Show", invocation.Method.Name);
        Assert.Equal("Test Device - Battery almost full (charging)", title);
        Assert.Equal($"Battery level: {batteryPercent}%", msg);
        Assert.Equal(NotificationType.Success, type);
    }
    
    [Fact]
    public async Task ShouldNotify_WhenLowTreshold()
    {
        // Arrange

        var appSettings = new AppSettings
        {
            Notifications = new NotificationSettings
            {
                Enabled = true,                
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var batteryPercent = 25;
        
        // Act

        var manager = await MockManager(batteryPercent, appSettings, PowerSupplyStatus.POWER_SUPPLY_STATUS_DISCHARGING);

        // Assert

        Assert.Single(manager.Invocations); // a single notification should be shown
        var invocation = manager.Invocations[0];
        var title = invocation.Arguments[0];
        var msg = invocation.Arguments[1];
        var type = (NotificationType)invocation.Arguments[2];

        Assert.Equal("Show", invocation.Method.Name);
        Assert.Equal("Test Device - Battery Low", title);
        Assert.Equal($"Battery level: {batteryPercent}%", msg);
        Assert.Equal(NotificationType.Warning, type);
    }
}
