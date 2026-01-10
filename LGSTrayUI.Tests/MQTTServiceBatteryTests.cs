using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayUI.Messages;
using LGSTrayUI.Services;
using LGSTrayUI.Tests.Mocks;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace LGSTrayUI.Tests;

/// <summary>
/// Tests for MQTT service battery reporting behavior.
/// </summary>
[Collection("Sequential")]
public class MQTTServiceBatteryTests
{

    private static LogiDeviceViewModel CreateTestDevice(string deviceId, string deviceName)
    {
        var appSettings = new AppSettings
        {
            UI = new UISettings(),
            Logging = new LoggingSettings { Enabled = false },
            HTTPServer = new HttpServerSettings { Enabled = false },
            GHub = new GHubManagerSettings { Enabled = false },
            Native = new NativeDeviceManagerSettings { Enabled = false },
            Notifications = new NotificationSettings()
        };

        var userSettings = new UserSettingsWrapper();
        var iconFactory = new MockLogiDeviceIconFactory();
        var device = new LogiDeviceViewModel(iconFactory, appSettings, userSettings);

        device.UpdateState(new InitMessage(
            deviceId: deviceId,
            deviceName: deviceName,
            hasBattery: true,
            deviceType: DeviceType.Mouse
        ));

        // Default to Native for tests (since we filter others now)
        device.DataSource = DataSource.Native;

        return device;
    }

    [Fact]
    public void PublishStateUpdate_Uninitialized_ReportsNegativeOne()
    {
        // Arrange - Device at startup with no battery data
        var device = CreateTestDevice("test001", "Test Mouse");
        device.BatteryPercentage = -1;
        device.IsOnline = false;

        // Act
        var stateJson = JsonSerializer.Serialize(new
        {
            percentage = (int)Math.Round(device.BatteryPercentage),
            voltage = device.BatteryVoltage,
            charging = device.PowerSupplyStatus == PowerSupplyStatus.CHARGING,
            online = device.IsOnline,
            is_wired = device.IsWiredMode,
            power_status = device.PowerSupplyStatus.ToString(),
            signature = device.DeviceSignature,
            last_update = device.LastUpdate.ToString("o"),
            device_name = device.DeviceName,
            device_type = device.DeviceType.ToString()
        });

        var parsed = JsonSerializer.Deserialize<JsonElement>(stateJson);

        // Assert
        Assert.Equal(-1, parsed.GetProperty("percentage").GetInt32());
        Assert.False(parsed.GetProperty("online").GetBoolean());
    }

    [Fact]
    public void PublishStateUpdate_ValidBattery_ReportsCorrectValue()
    {
        // Arrange - Device online with valid battery
        var device = CreateTestDevice("test002", "Test Keyboard");
        device.UpdateState(new UpdateMessage(
            deviceId: "test002",
            batteryPercentage: 75.5,
            powerSupplyStatus: PowerSupplyStatus.DISCHARGING,
            batteryMVolt: 3800,
            updateTime: DateTimeOffset.Now
        ));

        // Act
        var stateJson = JsonSerializer.Serialize(new
        {
            percentage = (int)Math.Round(device.BatteryPercentage),
            voltage = device.BatteryVoltage,
            charging = device.PowerSupplyStatus == PowerSupplyStatus.CHARGING,
            online = device.IsOnline,
            is_wired = device.IsWiredMode,
            power_status = device.PowerSupplyStatus.ToString(),
            signature = device.DeviceSignature,
            last_update = device.LastUpdate.ToString("o"),
            device_name = device.DeviceName,
            device_type = device.DeviceType.ToString()
        });

        var parsed = JsonSerializer.Deserialize<JsonElement>(stateJson);

        // Assert
        Assert.Equal(76, parsed.GetProperty("percentage").GetInt32()); // Rounded from 75.5
        Assert.Equal(3.8, parsed.GetProperty("voltage").GetDouble());
        Assert.True(parsed.GetProperty("online").GetBoolean());
        Assert.False(parsed.GetProperty("charging").GetBoolean());
    }

    [Fact]
    public void PublishStateUpdate_GoesOffline_PreservesLastKnownBattery()
    {
        // Arrange - Device with battery data
        var device = CreateTestDevice("test003", "Test Headset");
        device.UpdateState(new UpdateMessage(
            deviceId: "test003",
            batteryPercentage: 50.0,
            powerSupplyStatus: PowerSupplyStatus.DISCHARGING,
            batteryMVolt: 3700,
            updateTime: DateTimeOffset.Now
        ));

        // Simulate device going offline
        device.UpdateState(new UpdateMessage(
            deviceId: "test003",
            batteryPercentage: -1, // Negative indicates offline
            powerSupplyStatus: PowerSupplyStatus.UNKNOWN,
            batteryMVolt: 0,
            updateTime: DateTimeOffset.Now
        ));

        // Act
        var stateJson = JsonSerializer.Serialize(new
        {
            percentage = (int)Math.Round(device.BatteryPercentage),
            voltage = device.BatteryVoltage,
            charging = device.PowerSupplyStatus == PowerSupplyStatus.CHARGING,
            online = device.IsOnline,
            is_wired = device.IsWiredMode,
            power_status = device.PowerSupplyStatus.ToString(),
            signature = device.DeviceSignature,
            last_update = device.LastUpdate.ToString("o"),
            device_name = device.DeviceName,
            device_type = device.DeviceType.ToString()
        });

        var parsed = JsonSerializer.Deserialize<JsonElement>(stateJson);

        // Assert
        Assert.False(device.IsOnline);
        Assert.Equal(50.0, device.BatteryPercentage); // Preserved!
        Assert.Equal(50, parsed.GetProperty("percentage").GetInt32());
        Assert.False(parsed.GetProperty("online").GetBoolean());
    }

    [Fact]
    public void PublishStateUpdate_WiredModeWithoutBattery_ReportsNegativeOne()
    {
        // Arrange
        var device = CreateTestDevice("test004", "Test G515");
        device.UpdateState(new UpdateMessage(
            deviceId: "test004",
            batteryPercentage: -1, // Wired mode, no battery data
            powerSupplyStatus: PowerSupplyStatus.CHARGING,
            batteryMVolt: 0,
            updateTime: DateTimeOffset.Now,
            isWiredMode: true
        ));

        // Act
        var stateJson = JsonSerializer.Serialize(new
        {
            percentage = (int)Math.Round(device.BatteryPercentage),
            voltage = device.BatteryVoltage,
            charging = device.PowerSupplyStatus == PowerSupplyStatus.CHARGING,
            online = device.IsOnline,
            is_wired = device.IsWiredMode,
            power_status = device.PowerSupplyStatus.ToString(),
            signature = device.DeviceSignature,
            last_update = device.LastUpdate.ToString("o"),
            device_name = device.DeviceName,
            device_type = device.DeviceType.ToString()
        });

        var parsed = JsonSerializer.Deserialize<JsonElement>(stateJson);

        // Assert
        Assert.True(device.IsOnline);
        Assert.Equal(-1, parsed.GetProperty("percentage").GetInt32());
        Assert.True(parsed.GetProperty("charging").GetBoolean());
        Assert.True(parsed.GetProperty("is_wired").GetBoolean());
    }

    // Removed: PublishStateUpdate_MileageFromGHub_IncludedInPayload
    // (GHub reporting is now disabled and mileage removed from payload)
}