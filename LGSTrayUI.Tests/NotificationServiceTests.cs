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

public class NotificationServiceTests {
    private (NotificationService service, Mock<INotificationManager> manager, StrongReferenceMessenger messenger) CreateService(AppSettings appSettings) {
        var manager = new Mock<INotificationManager>(MockBehavior.Loose);
        var settings = new Mock<IOptions<AppSettings>>();
        var messenger = new StrongReferenceMessenger();

        settings.Setup(s => s.Value).Returns(appSettings);

        var service = new NotificationService(manager.Object, settings.Object, messenger);
        service.StartAsync(CancellationToken.None); // Start immediately

        return (service, manager, messenger);
    }

    private LogiDeviceViewModel CreateDevice(string id, string name, bool hasBattery = true) {
        var iconFactory = new Mock<ILogiDeviceIconFactory>();
        var settings = new Mock<IOptions<AppSettings>>();
        settings.Setup(s => s.Value).Returns(new AppSettings());
        var userSettings = new UserSettingsWrapper();

        // Fix: Use settings.Object.Value
        var device = new LogiDeviceViewModel(iconFactory.Object, settings.Object.Value, userSettings);
        device.UpdateState(new InitMessage(
            deviceId: id,
            deviceName: name,
            hasBattery: hasBattery,
            deviceType: DeviceType.Mouse
        ));
        return device;
    }

    private void UpdateBattery(LogiDeviceViewModel device, StrongReferenceMessenger messenger, int percentage, PowerSupplyStatus status = PowerSupplyStatus.DISCHARGING) {
        device.UpdateState(new UpdateMessage(
            deviceId: device.DeviceId,
            batteryPercentage: percentage,
            powerSupplyStatus: status,
            batteryMVolt: 3700,
            updateTime: DateTimeOffset.Now
        ));
        messenger.Send(new DeviceBatteryUpdatedMessage(device));
    }

    [Fact]
    public void ShouldNotify_WhenBatteryFull() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 90
            }
        };

        var (service, manager, messenger) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Initialize (not full)
        UpdateBattery(device, messenger, 50, PowerSupplyStatus.CHARGING);
        manager.Invocations.Clear(); // Clear init notifications if any

        // Act - Charge to full
        UpdateBattery(device, messenger, 100, PowerSupplyStatus.CHARGING);

        // Assert
        Assert.Single(manager.Invocations);
        var invocation = manager.Invocations[0];
        Assert.Equal("Show", invocation.Method.Name);
        Assert.Contains("Battery Full", (string)invocation.Arguments[0]);
        Assert.Contains("100%", (string)invocation.Arguments[1]);
        Assert.Equal(NotificationType.Success, invocation.Arguments[2]);
    }

    [Fact]
    public void ShouldNotify_WhenHighThreshold() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 80
            }
        };
        var (service, manager, messenger) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, messenger, 50, PowerSupplyStatus.CHARGING); // init
        manager.Invocations.Clear();

        UpdateBattery(device, messenger, 80, PowerSupplyStatus.CHARGING); // hit threshold

        // Assert
        Assert.Single(manager.Invocations);
        var invocation = manager.Invocations[0];
        Assert.Contains("almost full", (string)invocation.Arguments[0]);
        Assert.Contains("80%", (string)invocation.Arguments[1]);
        Assert.Equal(NotificationType.Success, invocation.Arguments[2]);
    }

    [Fact]
    public void ShouldNotify_WhenLowThreshold() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, messenger) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, messenger, 50); // init
        manager.Invocations.Clear();

        UpdateBattery(device, messenger, 30); // hit threshold

        // Assert
        Assert.Single(manager.Invocations);
        var invocation = manager.Invocations[0];
        Assert.Contains("Battery Low", (string)invocation.Arguments[0]);
        Assert.Contains("30%", (string)invocation.Arguments[1]);
        Assert.Equal(NotificationType.Warning, invocation.Arguments[2]);
    }

    [Fact]

    public void ShouldNotify_Sequentially_ForMultipleLowThresholds() {

        // Arrange

        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };

        // Thresholds will be [30, 10, 5]
        var (service, manager, messenger) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act 1: Initial (50%)
        UpdateBattery(device, messenger, 50);
        manager.Invocations.Clear();



        // Act 2: Drop to 30% -> Notify Warning
        UpdateBattery(device, messenger, 30);
        Assert.Single(manager.Invocations);
        Assert.Contains("30%", (string)manager.Invocations[0].Arguments[1]);
        Assert.Equal(NotificationType.Warning, manager.Invocations[0].Arguments[2]);
        manager.Invocations.Clear();

        // Act 3: Drop to 25% -> NO Notification (still in 30% band)
        UpdateBattery(device, messenger, 25);
        Assert.Empty(manager.Invocations);

        // Act 4: Drop to 10% -> Notify Warning
        UpdateBattery(device, messenger, 10);
        Assert.Single(manager.Invocations);
        Assert.Contains("10%", (string)manager.Invocations[0].Arguments[1]);
        Assert.Equal(NotificationType.Warning, manager.Invocations[0].Arguments[2]);

        manager.Invocations.Clear();

        // Act 5: Drop to 8% -> NO Notification
        UpdateBattery(device, messenger, 8);
        Assert.Empty(manager.Invocations);

        // Act 6: Drop to 5% -> Notify Error

        UpdateBattery(device, messenger, 5);
        Assert.Single(manager.Invocations);
        Assert.Contains("5%", (string)manager.Invocations[0].Arguments[1]);
        Assert.Equal(NotificationType.Error, manager.Invocations[0].Arguments[2]);
    }

    [Fact]
    public void ShouldNotNotify_WhenNotificationsDisabled() {
        
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = false, // Disabled globally
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };

        var (service, manager, messenger) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, messenger, 50);
        UpdateBattery(device, messenger, 5); // Critical low

        // Assert
        Assert.Empty(manager.Invocations);
    }

    [Fact]
    public void ShouldNotNotify_WhenStateChangeDisabled() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyStateChange = false // Disabled
            }
        };

        var (service, manager, messenger) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");
        UpdateBattery(device, messenger, 50); // Online
        manager.Invocations.Clear();
        
        // Act - Go Offline
        device.UpdateState(new UpdateMessage(
            deviceId: device.DeviceId,
            batteryPercentage: -1,
            powerSupplyStatus: PowerSupplyStatus.UNKNOWN,
            batteryMVolt: 0,
            updateTime: DateTimeOffset.Now
        ));
        messenger.Send(new DeviceBatteryUpdatedMessage(device));

        // Assert
        Assert.Empty(manager.Invocations);

        // Act - Go Online
        UpdateBattery(device, messenger, 50);

        // Assert
        Assert.Empty(manager.Invocations);

    }



}





