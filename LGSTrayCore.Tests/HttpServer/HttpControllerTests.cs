using LGSTrayCore;
using LGSTrayCore.HttpServer;
using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using Moq;
using System.Xml.Linq;
using Xunit;

namespace LGSTrayCore.Tests.HttpServer;

/// <summary>
/// Tests for HttpController battery reporting behavior
/// Ensures that battery data is reported correctly in different states:
/// - Startup with invalid data (-1) should report -1
/// - Device with valid battery that goes offline should report last good value
/// </summary>
public class HttpControllerTests
{
    private class TestLogiDevice : LogiDevice
    {
        public TestLogiDevice()
        {
            DeviceId = "test001";
            DeviceName = "Test Device";
            DeviceType = DeviceType.Mouse;
            DataSource = DataSource.Native;
        }
    }

    [Fact]
    public void GetXmlData_Uninitialized_ReportsNegativeOne()
    {
        // Arrange - Device at startup with no battery data
        var device = new TestLogiDevice
        {
            BatteryPercentage = -1,
            IsOnline = false
        };

        // Act
        var xml = device.GetXmlData();

        // Assert
        var doc = XDocument.Parse(xml);
        var batteryPercent = doc.Root?.Element("battery_percent")?.Value;
        var isOnline = doc.Root?.Element("is_online")?.Value;

        Assert.Equal("-1.00", batteryPercent);
        Assert.Equal("False", isOnline);
    }

    [Fact]
    public void GetXmlData_ValidBattery_ReportsCorrectValue()
    {
        // Arrange - Device online with valid battery
        var device = new TestLogiDevice
        {
            BatteryPercentage = 75.5,
            BatteryVoltage = 3.8,
            IsOnline = true
        };

        // Act
        var xml = device.GetXmlData();

        // Assert
        var doc = XDocument.Parse(xml);
        var batteryPercent = doc.Root?.Element("battery_percent")?.Value;
        var batteryVoltage = doc.Root?.Element("battery_voltage")?.Value;
        var isOnline = doc.Root?.Element("is_online")?.Value;

        Assert.Equal("75.50", batteryPercent);
        Assert.Equal("3.80", batteryVoltage);
        Assert.Equal("True", isOnline);
    }

    [Fact]
    public void GetXmlData_GoesOffline_PreservesLastKnownBattery()
    {
        // Arrange - Device with battery data that then goes offline
        var device = new TestLogiDevice
        {
            BatteryPercentage = 50.0,
            BatteryVoltage = 3.7,
            IsOnline = true
        };

        // Simulate device going offline (BatteryPercentage NOT updated to -1)
        device.IsOnline = false;

        // Act
        var xml = device.GetXmlData();

        // Assert
        var doc = XDocument.Parse(xml);
        var batteryPercent = doc.Root?.Element("battery_percent")?.Value;
        var isOnline = doc.Root?.Element("is_online")?.Value;

        // Should report last known battery (50.00), not -1
        Assert.Equal("50.00", batteryPercent);
        Assert.Equal("False", isOnline);
    }

    [Fact]
    public void GetXmlData_WiredModeWithoutBattery_ReportsNegativeOne()
    {
        // Arrange - Wired device that doesn't report battery percentage
        var device = new TestLogiDevice
        {
            BatteryPercentage = -1,
            IsOnline = true,
            IsWiredMode = true,
            PowerSupplyStatus = PowerSupplyStatus.CHARGING
        };

        // Act
        var xml = device.GetXmlData();

        // Assert
        var doc = XDocument.Parse(xml);
        var batteryPercent = doc.Root?.Element("battery_percent")?.Value;
        var isOnline = doc.Root?.Element("is_online")?.Value;
        var charging = doc.Root?.Element("charging")?.Value;

        // Wired mode without battery data should report -1
        Assert.Equal("-1.00", batteryPercent);
        Assert.Equal("True", isOnline);
        Assert.Equal("True", charging);
    }

    [Fact]
    public void GetXmlData_XmlStructure_IsValid()
    {
        // Arrange
        var device = new TestLogiDevice
        {
            BatteryPercentage = 80.0,
            BatteryVoltage = 3.9,
            BatteryMileage = 120.5,
            PowerSupplyStatus = PowerSupplyStatus.DISCHARGING,
            IsOnline = true,
            LastUpdate = new DateTimeOffset(2025, 1, 8, 12, 0, 0, TimeSpan.Zero)
        };

        // Act
        var xml = device.GetXmlData();

        // Assert - Verify XML structure
        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Root);
        Assert.Equal("xml", doc.Root.Name.LocalName);

        // Verify all expected elements exist
        Assert.NotNull(doc.Root.Element("device_id"));
        Assert.NotNull(doc.Root.Element("device_name"));
        Assert.NotNull(doc.Root.Element("device_type"));
        Assert.NotNull(doc.Root.Element("is_online"));
        Assert.NotNull(doc.Root.Element("battery_percent"));
        Assert.NotNull(doc.Root.Element("battery_voltage"));
        Assert.NotNull(doc.Root.Element("mileage"));
        Assert.NotNull(doc.Root.Element("charging"));
        Assert.NotNull(doc.Root.Element("last_update"));
    }

    [Fact]
    public void GetXmlData_ChargingStatus_ReportsCorrectly()
    {
        // Arrange - Charging device
        var device = new TestLogiDevice
        {
            BatteryPercentage = 85.0,
            PowerSupplyStatus = PowerSupplyStatus.CHARGING,
            IsOnline = true
        };

        // Act
        var xml = device.GetXmlData();

        // Assert
        var doc = XDocument.Parse(xml);
        var charging = doc.Root?.Element("charging")?.Value;
        Assert.Equal("True", charging);

        // Change to discharging
        device.PowerSupplyStatus = PowerSupplyStatus.DISCHARGING;
        xml = device.GetXmlData();
        doc = XDocument.Parse(xml);
        charging = doc.Root?.Element("charging")?.Value;
        Assert.Equal("False", charging);
    }
}
