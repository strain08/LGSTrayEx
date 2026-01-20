using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayUI.Interfaces;
using LGSTrayUI.Messages;
using LGSTrayUI.Services;
using MessagePipe;
using Microsoft.Extensions.Options;
using Moq;
using Notification.Wpf;

namespace LGSTrayUI.Tests;

public class NotificationServiceTests {

    private class MockDisposable : IDisposable { public void Dispose() { } }

    private (NotificationService service, 
             Mock<INotificationManager> manager, 
             Action<DeviceBatteryUpdatedMessage> batteryHandler,
             Action<SystemSuspendingMessage> suspendHandler,
             Action<SystemResumingMessage> resumeHandler) 
    CreateService(AppSettings appSettings) {
        var manager = new Mock<INotificationManager>(MockBehavior.Loose);
        var settings = new Mock<IOptions<AppSettings>>();
        
        settings.Setup(s => s.Value).Returns(appSettings);

        var batterySubscriber = new Mock<ISubscriber<DeviceBatteryUpdatedMessage>>();
        IMessageHandler<DeviceBatteryUpdatedMessage> capturedBatteryHandler = null!;
        batterySubscriber.Setup(s => s.Subscribe(It.IsAny<IMessageHandler<DeviceBatteryUpdatedMessage>>(), It.IsAny<MessageHandlerFilter<DeviceBatteryUpdatedMessage>[]>()))
            .Callback<IMessageHandler<DeviceBatteryUpdatedMessage>, MessageHandlerFilter<DeviceBatteryUpdatedMessage>[]>((h, _) => capturedBatteryHandler = h)
            .Returns(new MockDisposable());

        var suspendSubscriber = new Mock<ISubscriber<SystemSuspendingMessage>>();
        IMessageHandler<SystemSuspendingMessage> capturedSuspendHandler = null!;
        suspendSubscriber.Setup(s => s.Subscribe(It.IsAny<IMessageHandler<SystemSuspendingMessage>>(), It.IsAny<MessageHandlerFilter<SystemSuspendingMessage>[]>()))
            .Callback<IMessageHandler<SystemSuspendingMessage>, MessageHandlerFilter<SystemSuspendingMessage>[]>((h, _) => capturedSuspendHandler = h)
            .Returns(new MockDisposable());

        var resumeSubscriber = new Mock<ISubscriber<SystemResumingMessage>>();
        IMessageHandler<SystemResumingMessage> capturedResumeHandler = null!;
        resumeSubscriber.Setup(s => s.Subscribe(It.IsAny<IMessageHandler<SystemResumingMessage>>(), It.IsAny<MessageHandlerFilter<SystemResumingMessage>[]>()))
            .Callback<IMessageHandler<SystemResumingMessage>, MessageHandlerFilter<SystemResumingMessage>[]>((h, _) => capturedResumeHandler = h)
            .Returns(new MockDisposable());

        var service = new NotificationService(
            manager.Object, 
            settings.Object, 
            batterySubscriber.Object,
            suspendSubscriber.Object,
            resumeSubscriber.Object);
            
        service.StartAsync(CancellationToken.None); // Start immediately (wires up subscriptions)

        return (service, manager, (msg) => capturedBatteryHandler?.Handle(msg), (msg) => capturedSuspendHandler?.Handle(msg), (msg) => capturedResumeHandler?.Handle(msg));
    }

    private LogiDeviceViewModel CreateDevice(string id, string name, bool hasBattery = true, AppSettings? appSettings = null) {
        var iconFactory = new Mock<ILogiDeviceIconFactory>();
        var settings = new Mock<IOptions<AppSettings>>();

        // Use provided appSettings or create default with initialized Notifications
        var settingsValue = appSettings ?? new AppSettings {
            Notifications = new NotificationSettings()
        };
        settings.Setup(s => s.Value).Returns(settingsValue);
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

    private void UpdateBattery(LogiDeviceViewModel device, Action<DeviceBatteryUpdatedMessage> handler, int percentage, PowerSupplyStatus status = PowerSupplyStatus.DISCHARGING) {
        device.UpdateState(new UpdateMessage(
            deviceId: device.DeviceId,
            batteryPercentage: percentage,
            powerSupplyStatus: status,
            batteryMVolt: 3700,
            updateTime: DateTimeOffset.Now
        ));
        handler(new DeviceBatteryUpdatedMessage(device));
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

        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Initialize (not full)
        UpdateBattery(device, batteryHandler, 50, PowerSupplyStatus.CHARGING);
        manager.Invocations.Clear(); // Clear init notifications if any

        // Act - Charge to full
        UpdateBattery(device, batteryHandler, 100, PowerSupplyStatus.CHARGING);

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
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50, PowerSupplyStatus.CHARGING); // init
        manager.Invocations.Clear();

        UpdateBattery(device, batteryHandler, 80, PowerSupplyStatus.CHARGING); // hit threshold

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
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50); // init
        manager.Invocations.Clear();

        UpdateBattery(device, batteryHandler, 30); // hit threshold

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
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act 1: Initial (50%)
        UpdateBattery(device, batteryHandler, 50);
        manager.Invocations.Clear();



        // Act 2: Drop to 30% -> Notify Warning
        UpdateBattery(device, batteryHandler, 30);
        Assert.Single(manager.Invocations);
        Assert.Contains("30%", (string)manager.Invocations[0].Arguments[1]);
        Assert.Equal(NotificationType.Warning, manager.Invocations[0].Arguments[2]);
        manager.Invocations.Clear();

        // Act 3: Drop to 25% -> NO Notification (still in 30% band)
        UpdateBattery(device, batteryHandler, 25);
        Assert.Empty(manager.Invocations);

        // Act 4: Drop to 10% -> Notify Warning
        UpdateBattery(device, batteryHandler, 10);
        Assert.Single(manager.Invocations);
        Assert.Contains("10%", (string)manager.Invocations[0].Arguments[1]);
        Assert.Equal(NotificationType.Warning, manager.Invocations[0].Arguments[2]);

        manager.Invocations.Clear();

        // Act 5: Drop to 8% -> NO Notification
        UpdateBattery(device, batteryHandler, 8);
        Assert.Empty(manager.Invocations);

        // Act 6: Drop to 5% -> Notify Error

        UpdateBattery(device, batteryHandler, 5);
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

        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50);
        UpdateBattery(device, batteryHandler, 5); // Critical low

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

        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device", hasBattery: true, appSettings: appSettings);
        UpdateBattery(device, batteryHandler, 50); // Online
        manager.Invocations.Clear();

        // Act - Go Offline
        device.UpdateState(new UpdateMessage(
            deviceId: device.DeviceId,
            batteryPercentage: -1,
            powerSupplyStatus: PowerSupplyStatus.UNKNOWN,
            batteryMVolt: 0,
            updateTime: DateTimeOffset.Now
        ));
        batteryHandler(new DeviceBatteryUpdatedMessage(device));

        // Assert
        Assert.Empty(manager.Invocations);

        // Act - Go Online
        UpdateBattery(device, batteryHandler, 50);

        // Assert
        Assert.Empty(manager.Invocations);
    }

    // ==================== Critical Battery Level Tests ====================

    [Fact]
    public void ShouldNotify_Critical1_WhenBatteryAt5Percent() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50); // init
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 5); // critical1

        // Assert
        Assert.Single(manager.Invocations);
        var invocation = manager.Invocations[0];
        Assert.Contains("Test Device - Battery Low", (string)invocation.Arguments[0]);
        Assert.Contains("5%", (string)invocation.Arguments[1]);
        Assert.Equal(NotificationType.Error, invocation.Arguments[2]);
    }

    [Fact]
    public void ShouldNotify_Critical2_WhenBatteryAt10Percent() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50); // init
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 10); // critical2

        // Assert
        Assert.Single(manager.Invocations);
        var invocation = manager.Invocations[0];
        Assert.Contains("Test Device - Battery Low", (string)invocation.Arguments[0]);
        Assert.Contains("10%", (string)invocation.Arguments[1]);
        Assert.Equal(NotificationType.Warning, invocation.Arguments[2]);
    }

    [Fact]
    public void ShouldNotNotify_Critical_WhenCharging() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50, PowerSupplyStatus.CHARGING); // init
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 5, PowerSupplyStatus.CHARGING); // critical but charging

        // Assert - should not notify because device is charging
        Assert.Empty(manager.Invocations);
    }

    // ==================== Charging State Edge Cases ====================

    [Fact]
    public void ShouldNotNotify_HighBattery_WhenDischarging() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 90
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Battery at 95% but discharging
        UpdateBattery(device, batteryHandler, 50, PowerSupplyStatus.DISCHARGING); // init
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 95, PowerSupplyStatus.DISCHARGING);

        // Assert - High range only notifies when charging
        Assert.Empty(manager.Invocations);
    }

    [Fact]
    public void ShouldNotNotify_FullBattery_WhenDischarging() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 90
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Battery at 100% but discharging
        UpdateBattery(device, batteryHandler, 50, PowerSupplyStatus.DISCHARGING); // init
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 100, PowerSupplyStatus.DISCHARGING);

        // Assert - Full only notifies when charging
        Assert.Empty(manager.Invocations);
    }

    [Fact]
    public void ShouldNotify_WhenStartChargingAtHighBattery() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 90
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Already at 95%, now start charging
        UpdateBattery(device, batteryHandler, 95, PowerSupplyStatus.DISCHARGING); // init at high
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 95, PowerSupplyStatus.CHARGING); // start charging

        // Assert - Should notify because we started charging in High range
        Assert.Single(manager.Invocations);
        Assert.Contains("almost full", (string)manager.Invocations[0].Arguments[0]);
    }

    [Fact]
    public void ShouldNotify_WhenStartChargingAtFullBattery() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 90
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Already at 100%, now start charging
        UpdateBattery(device, batteryHandler, 100, PowerSupplyStatus.DISCHARGING); // init at full
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 100, PowerSupplyStatus.CHARGING); // start charging

        // Assert - Should notify Battery Full
        Assert.Single(manager.Invocations);
        Assert.Contains("Battery Full", (string)manager.Invocations[0].Arguments[0]);
    }

    [Fact]
    public void ShouldNotNotify_WhenStopChargingAtHighBattery() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 90
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Charging at 95%, then stop charging
        UpdateBattery(device, batteryHandler, 95, PowerSupplyStatus.CHARGING); // init
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 95, PowerSupplyStatus.DISCHARGING); // stop charging

        // Assert - Should not notify when stopping charging
        Assert.Empty(manager.Invocations);
    }

    // ==================== System Suspend/Resume Tests ====================

    [Fact]
    public void ShouldNotNotify_WhenSystemSuspended() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, batteryHandler, suspendHandler, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        UpdateBattery(device, batteryHandler, 50); // init
        manager.Invocations.Clear();

        // Act - System suspending
        suspendHandler(new SystemSuspendingMessage());

        // Try to trigger notification while suspended
        UpdateBattery(device, batteryHandler, 5);

        // Assert - Should not notify during suspend
        Assert.Empty(manager.Invocations);
    }

    [Fact]
    public void ShouldNotify_AfterSystemResume() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, batteryHandler, suspendHandler, resumeHandler) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        UpdateBattery(device, batteryHandler, 50); // init
        suspendHandler(new SystemSuspendingMessage()); // suspend
        resumeHandler(new SystemResumingMessage()); // resume
        manager.Invocations.Clear();

        // Act - Trigger notification after resume
        UpdateBattery(device, batteryHandler, 5);

        // Assert - Should notify after resume (battery notifications work)
        Assert.Single(manager.Invocations);
    }

    [Fact]
    public void ShouldNotNotify_Offline_DuringResumeGracePeriod() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyStateChange = true
            }
        };
        var (service, manager, batteryHandler, suspendHandler, resumeHandler) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        UpdateBattery(device, batteryHandler, 50); // online
        suspendHandler(new SystemSuspendingMessage()); // suspend
        resumeHandler(new SystemResumingMessage()); // resume
        manager.Invocations.Clear();

        // Act - Device goes offline within grace period (10s)
        device.UpdateState(new UpdateMessage(
            deviceId: device.DeviceId,
            batteryPercentage: -1,
            powerSupplyStatus: PowerSupplyStatus.UNKNOWN,
            batteryMVolt: 0,
            updateTime: DateTimeOffset.Now
        ));
        batteryHandler(new DeviceBatteryUpdatedMessage(device));

        // Assert - Offline notification suppressed during grace period
        Assert.Empty(manager.Invocations);
    }

    // ==================== Same Range Updates ====================

    [Fact]
    public void ShouldNotNotify_WhenBatteryChangesWithinSameRange() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 90
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Start at 92% (High range)
        UpdateBattery(device, batteryHandler, 50, PowerSupplyStatus.CHARGING);
        UpdateBattery(device, batteryHandler, 92, PowerSupplyStatus.CHARGING);
        manager.Invocations.Clear();

        // Change to 95% - still in High range
        UpdateBattery(device, batteryHandler, 95, PowerSupplyStatus.CHARGING);

        // Assert - No notification because still in same range
        Assert.Empty(manager.Invocations);
    }

    [Fact]
    public void ShouldNotNotify_WhenBatteryChangesWithinLowRange() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Drop to Low range (30%)
        UpdateBattery(device, batteryHandler, 50);
        UpdateBattery(device, batteryHandler, 30);
        manager.Invocations.Clear();

        // Change within Low range (30 -> 25 -> 20)
        UpdateBattery(device, batteryHandler, 25);
        UpdateBattery(device, batteryHandler, 20);

        // Assert - No notifications within same range
        Assert.Empty(manager.Invocations);
    }

    // ==================== Feature Flag Tests ====================

    [Fact]
    public void ShouldNotNotify_High_WhenNotifyOnBatteryHighDisabled() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = false, // Disabled
                BatteryHighThreshold = 90
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50, PowerSupplyStatus.CHARGING);
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 95, PowerSupplyStatus.CHARGING); // High range

        // Assert - No notification because NotifyOnBatteryHigh is disabled
        Assert.Empty(manager.Invocations);
    }

    [Fact]
    public void ShouldStillNotify_Full_WhenNotifyOnBatteryHighDisabled() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = false, // Disabled
                BatteryHighThreshold = 90
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50, PowerSupplyStatus.CHARGING);
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 100, PowerSupplyStatus.CHARGING); // Full

        // Assert - Full should still notify even when NotifyOnBatteryHigh is disabled
        Assert.Single(manager.Invocations);
        Assert.Contains("Battery Full", (string)manager.Invocations[0].Arguments[0]);
    }

    [Fact]
    public void ShouldNotNotify_Low_WhenNotifyOnBatteryLowDisabled() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = false, // Disabled
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50);
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 30); // Low
        UpdateBattery(device, batteryHandler, 15); // Critical2
        UpdateBattery(device, batteryHandler, 5);  // Critical1

        // Assert - No notifications for low/critical when disabled
        Assert.Empty(manager.Invocations);
    }

    // ==================== Boundary Condition Tests ====================

    [Fact]
    public void ShouldNotify_AtExactLowThreshold() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50);
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 30); // Exactly at threshold

        // Assert
        Assert.Single(manager.Invocations);
        Assert.Contains("30%", (string)manager.Invocations[0].Arguments[1]);
    }

    [Fact]
    public void ShouldNotify_AtExactHighThreshold() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryHigh = true,
                BatteryHighThreshold = 90
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act
        UpdateBattery(device, batteryHandler, 50, PowerSupplyStatus.CHARGING);
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 90, PowerSupplyStatus.CHARGING); // Exactly at threshold

        // Assert
        Assert.Single(manager.Invocations);
        Assert.Contains("90%", (string)manager.Invocations[0].Arguments[1]);
    }

    [Fact]
    public void ShouldNotify_AtRangeBoundary_15To16_Discharging() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 15
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Start at 16% (Low range, but above threshold of 15)
        UpdateBattery(device, batteryHandler, 16);
        manager.Invocations.Clear();

        // Move to 15% (Critical2/Threshold boundary) while discharging
        UpdateBattery(device, batteryHandler, 15, PowerSupplyStatus.DISCHARGING);

        // Assert - Should notify entering threshold range
        Assert.Single(manager.Invocations);
        Assert.Contains("15%", (string)manager.Invocations[0].Arguments[1]);
    }

    [Fact]
    public void ShouldNotify_AtRangeBoundary_5To6_Discharging() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyOnBatteryLow = true,
                BatteryLowThreshold = 30
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Device");

        // Act - Drop from 50% to 6% (Critical2)
        UpdateBattery(device, batteryHandler, 50);
        manager.Invocations.Clear();
        UpdateBattery(device, batteryHandler, 6, PowerSupplyStatus.DISCHARGING);

        // Assert - Should notify entering Critical2 range
        Assert.Single(manager.Invocations);
        Assert.Contains("6%", (string)manager.Invocations[0].Arguments[1]);
    }

    // ==================== Device State Transition Tests ====================

    [Fact]
    public void ShouldNotify_WhenDeviceGoesOffline() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyStateChange = true,
                SuppressModeSwitchNotifications = false // Disable delay for immediate notification
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Mouse");

        UpdateBattery(device, batteryHandler, 50); // Online
        manager.Invocations.Clear();

        // Act - Device goes offline
        device.UpdateState(new UpdateMessage(
            deviceId: device.DeviceId,
            batteryPercentage: -1,
            powerSupplyStatus: PowerSupplyStatus.UNKNOWN,
            batteryMVolt: 0,
            updateTime: DateTimeOffset.Now
        ));
        batteryHandler(new DeviceBatteryUpdatedMessage(device));

        // Assert
        Assert.Single(manager.Invocations);
        var invocation = manager.Invocations[0];
        Assert.Contains("Offline", (string)invocation.Arguments[0]);
        Assert.Contains("Test Mouse", (string)invocation.Arguments[0]);
    }

    [Fact]
    public void ShouldNotify_WhenDeviceComesOnline() {
        // Arrange
        var appSettings = new AppSettings {
            Notifications = new NotificationSettings {
                Enabled = true,
                NotifyStateChange = true,
                SuppressModeSwitchNotifications = false // Disable delay for immediate notification
            }
        };
        var (service, manager, batteryHandler, _, _) = CreateService(appSettings);
        var device = CreateDevice("dev1", "Test Mouse");

        // Start offline
        device.UpdateState(new UpdateMessage(
            deviceId: device.DeviceId,
            batteryPercentage: -1,
            powerSupplyStatus: PowerSupplyStatus.UNKNOWN,
            batteryMVolt: 0,
            updateTime: DateTimeOffset.Now
        ));
        batteryHandler(new DeviceBatteryUpdatedMessage(device));
        manager.Invocations.Clear();

        // Act - Device comes online
        UpdateBattery(device, batteryHandler, 50);

        // Assert
        Assert.Single(manager.Invocations);
        var invocation = manager.Invocations[0];
        Assert.Contains("Online", (string)invocation.Arguments[0]);
        Assert.Contains("Test Mouse", (string)invocation.Arguments[0]);
    }
}





