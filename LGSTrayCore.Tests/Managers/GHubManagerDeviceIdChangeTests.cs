using LGSTrayCore.Managers;
using LGSTrayCore.Tests.Mocks;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayPrimitives;

namespace LGSTrayCore.Tests.Managers;

/// <summary>
/// Tests for GHUB device ID change issue after sleep/wake cycles
/// These tests verify that the implementation fixes the duplicate device issue
/// </summary>
public class GHubManagerDeviceIdChangeTests
{
    [Fact]
    public async Task SleepWakeCycle_DeviceIdChanges_PublishesRemoveAndInit()
    {
        // This test simulates the exact problem:
        // 1. Device initially discovered as dev00000001
        // 2. System sleeps (device disconnects)
        // 3. Device wakes with new ID dev00000002
        // Expected: RemoveMessage for old ID, InitMessage for new ID

        // Arrange
        var mockPublisher = new MockPublisher<IPCMessage>();
        var mockWs = new MockWebSocketClient();
        var mockFactory = new MockWebSocketClientFactory(mockWs);
        var manager = new GHubManager(mockPublisher, mockFactory);

        await manager.StartAsync(CancellationToken.None);

        // Simulate initial device discovery
        mockWs.SimulateDeviceListResponse(("dev00000001", "G Pro Wireless", true));
        await Task.Delay(50); // Let async propagate

        mockPublisher.PublishedMessages.Clear(); // Reset to track sleep/wake messages

        // Act: Simulate sleep/wake cycle with ID change
        // Step 1: Device disconnects (sleep)
        mockWs.SimulateDeviceStateChange("dev00000001", "disconnected");
        await Task.Delay(50);

        // Step 2: Device reconnects with new ID (wake)
        mockWs.SimulateDeviceStateChange("dev00000002", "connected");
        await Task.Delay(50);

        // GHUB sends new device list with new ID
        mockWs.SimulateDeviceListResponse(("dev00000002", "G Pro Wireless", true));
        await Task.Delay(50);

        // Assert
        var removeMsg = mockPublisher.PublishedMessages.OfType<RemoveMessage>()
            .FirstOrDefault(m => m.deviceId == "dev00000001");
        var initMsg = mockPublisher.PublishedMessages.OfType<InitMessage>()
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
        // with state="disconnected", we publish a RemoveMessage

        // Arrange
        var mockPublisher = new MockPublisher<IPCMessage>();
        var mockWs = new MockWebSocketClient();
        var mockFactory = new MockWebSocketClientFactory(mockWs);
        var manager = new GHubManager(mockPublisher, mockFactory);

        await manager.StartAsync(CancellationToken.None);

        // Act
        mockWs.SimulateDeviceStateChange("dev00000001", "disconnected");
        await Task.Delay(50); // Let async propagate

        // Assert
        var removeMsg = mockPublisher.PublishedMessages.OfType<RemoveMessage>()
            .FirstOrDefault(m => m.deviceId == "dev00000001");

        Assert.NotNull(removeMsg);
        Assert.Equal("ghub_disconnect", removeMsg.reason);
    }

    [Fact]
    public async Task DeviceConnected_RequestsDeviceInfo()
    {
        // This test verifies that when GHUB sends /devices/state/changed
        // with state="connected", we request device info to trigger re-registration

        // Arrange
        var mockPublisher = new MockPublisher<IPCMessage>();
        var mockWs = new MockWebSocketClient();
        var mockFactory = new MockWebSocketClientFactory(mockWs);
        var manager = new GHubManager(mockPublisher, mockFactory);

        await manager.StartAsync(CancellationToken.None);

        mockWs.SentMessages.Clear(); // Clear startup messages

        // Act
        mockWs.SimulateDeviceStateChange("dev00000002", "connected");
        await Task.Delay(50);

        // Assert
        var deviceRequest = mockWs.SentMessages.FirstOrDefault(m => m.Contains("/devices/dev00000002"));
        Assert.NotNull(deviceRequest);
        Assert.Contains("\"path\":\"/devices/dev00000002\"", deviceRequest);
    }

    [Fact]
    public async Task RediscoverDevices_PublishesWildcardRemoval()
    {
        // This test verifies that RediscoverDevices() clears all GHUB devices
        // before re-discovering them to prevent duplicates

        // Arrange
        var mockPublisher = new MockPublisher<IPCMessage>();
        var mockWs = new MockWebSocketClient();
        var mockFactory = new MockWebSocketClientFactory(mockWs);
        var manager = new GHubManager(mockPublisher, mockFactory);

        await manager.StartAsync(CancellationToken.None);

        // Simulate initial devices
        mockWs.SimulateDeviceListResponse(
            ("dev00000001", "Device 1", true),
            ("dev00000002", "Device 2", true)
        );
        await Task.Delay(50);

        mockPublisher.PublishedMessages.Clear();

        // Act
        manager.RediscoverDevices();
        await Task.Delay(200); // Wait for cleanup + reconnect

        // Assert
        var wildcardRemoval = mockPublisher.PublishedMessages.OfType<RemoveMessage>()
            .FirstOrDefault(m => m.deviceId == "*GHUB*");

        Assert.NotNull(wildcardRemoval);
        Assert.Equal("rediscover_cleanup", wildcardRemoval.reason);
    }
}
