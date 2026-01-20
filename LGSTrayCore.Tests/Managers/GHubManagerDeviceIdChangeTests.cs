using LGSTrayCore.Managers;
using LGSTrayCore.Tests.Mocks;
using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using Moq;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LGSTrayCore.Tests.Managers;

/// <summary>
/// Tests for GHUB device ID change issue after sleep/wake cycles
/// These tests verify that the implementation fixes the duplicate device issue
/// </summary>
public class GHubManagerDeviceIdChangeTests
{
    private (GHubManager manager, Mock<IPublisher<IPCMessage>> publisherMock, MockWebSocketClient mockWs, List<IPCMessage> publishedMessages) CreateTestContext()
    {
        var publisherMock = new Mock<IPublisher<IPCMessage>>();
        var publishedMessages = new List<IPCMessage>();
        publisherMock.Setup(p => p.Publish(It.IsAny<IPCMessage>()))
            .Callback<IPCMessage>(msg => publishedMessages.Add(msg));

        var mockWs = new MockWebSocketClient();
        var mockFactory = new MockWebSocketClientFactory(mockWs);
        var manager = new GHubManager(publisherMock.Object, mockFactory);

        return (manager, publisherMock, mockWs, publishedMessages);
    }

    [Fact]
    public async Task SleepWakeCycle_DeviceIdChanges_PublishesRemoveAndInit()
    {
        // This test simulates the exact problem:
        // 1. Device initially discovered as dev00000001
        // 2. System sleeps (device disconnects)
        // 3. Device wakes with new ID dev00000002
        // Expected: RemoveMessage for old ID, InitMessage for new ID

        // Arrange
        var (manager, _, mockWs, publishedMessages) = CreateTestContext();

        // Register device info for auto-response
        mockWs.RegisterDeviceInfo("dev00000001", "G Pro Wireless");
        mockWs.RegisterDeviceInfo("dev00000002", "G Pro Wireless");

        await manager.StartAsync(CancellationToken.None);

        // Simulate initial device discovery
        mockWs.SimulateDeviceListResponse(("dev00000001", "G Pro Wireless", true));
        await Task.Delay(50); // Let async propagate

        publishedMessages.Clear(); // Reset to track sleep/wake messages

        // Act: Simulate sleep/wake cycle with ID change
        // Step 1: Device disconnects (sleep)
        mockWs.SimulateDeviceStateChange("dev00000001", "not_connected");
        await Task.Delay(50);

        // Step 2: Device reconnects with new ID (wake) - GHUB sends full device info in state change
        mockWs.SimulateDeviceStateChange("dev00000002", "active", includeDeviceInfo: true);
        await Task.Delay(100); // Wait for processing

        // Assert
        var removeMsg = publishedMessages.OfType<RemoveMessage>()
            .FirstOrDefault(m => m.deviceId == "dev00000001");
        var initMsg = publishedMessages.OfType<InitMessage>()
            .FirstOrDefault(m => m.deviceId == "dev00000002");

        Assert.NotNull(removeMsg);
        Assert.Equal("ghub_disconnect", removeMsg.reason);

        Assert.NotNull(initMsg);
        Assert.Equal("G Pro Wireless", initMsg.deviceName);
    }

    [Fact]
    public async Task DeviceDisconnected_PublishesRemoveMessage()
    {
        // This test verifies that when GHUB sends /devices/state/changed
        // with state="not_connected", we publish a RemoveMessage

        // Arrange
        var (manager, _, mockWs, publishedMessages) = CreateTestContext();

        await manager.StartAsync(CancellationToken.None);

        await Task.Delay(50); // Let startup complete
        // Act
        mockWs.SimulateDeviceStateChange("dev00000001", "not_connected");
        await Task.Delay(50); // Let async propagate

        // Assert
        var removeMsg = publishedMessages.OfType<RemoveMessage>()
            .FirstOrDefault(m => m.deviceId == "dev00000001");

        Assert.NotNull(removeMsg);
        Assert.Equal("ghub_disconnect", removeMsg.reason);
    }

    [Fact]
    public async Task DeviceConnected_WithDeviceInfo_PublishesInitMessage()
    {
        // This test verifies that when GHUB sends /devices/state/changed
        // with state="active" and full device info, we re-register the device

        // Arrange
        var (manager, _, mockWs, publishedMessages) = CreateTestContext();

        // Register device info for state change simulation
        mockWs.RegisterDeviceInfo("dev00000002", "Test Device");

        await manager.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        publishedMessages.Clear(); // Clear startup messages

        // Act - simulate device reconnect with full device info in state change
        mockWs.SimulateDeviceStateChange("dev00000002", "active", includeDeviceInfo: true);
        await Task.Delay(50);

        // Assert - device should be re-registered
        var initMsg = publishedMessages.OfType<InitMessage>()
            .FirstOrDefault(m => m.deviceId == "dev00000002");

        Assert.NotNull(initMsg);
        Assert.Equal("Test Device", initMsg.deviceName);
    }

    [Fact]
    public async Task RediscoverDevices_PublishesWildcardRemoval()
    {
        // This test verifies that RediscoverDevices() clears all GHUB devices
        // before re-discovering them to prevent duplicates

        // Arrange
        var (manager, _, mockWs, publishedMessages) = CreateTestContext();

        await manager.StartAsync(CancellationToken.None);

        // Simulate initial devices
        mockWs.SimulateDeviceListResponse(
            ("dev00000001", "Device 1", true),
            ("dev00000002", "Device 2", true)
        );
        await Task.Delay(50);

        publishedMessages.Clear();

        // Act
        _ = manager.RediscoverDevices();
        await Task.Delay(200); // Wait for cleanup + reconnect

        // Assert
        var wildcardRemoval = publishedMessages.OfType<RemoveMessage>()
            .FirstOrDefault(m => m.deviceId == "*GHUB*");

        Assert.NotNull(wildcardRemoval);
        Assert.Equal("rediscover_cleanup", wildcardRemoval.reason);
    }
}
