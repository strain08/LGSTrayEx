using LGSTrayPrimitives.MessageStructs;
using MessagePack;

namespace LGSTrayPrimitives.Tests;

public class MessageStructsTests
{
    [Fact]
    public void RemoveMessage_Serialization_RoundTrip()
    {
        // Arrange
        var original = new RemoveMessage("testDevice123", "test_reason");

        // Act
        var bytes = MessagePackSerializer.Serialize<IPCMessage>(original);
        var deserialized = MessagePackSerializer.Deserialize<IPCMessage>(bytes);

        // Assert
        Assert.IsType<RemoveMessage>(deserialized);
        var removeMsg = (RemoveMessage)deserialized;
        Assert.Equal("testDevice123", removeMsg.deviceId);
        Assert.Equal("test_reason", removeMsg.reason);
    }

    [Fact]
    public void RemoveMessage_Serialization_WithoutReason()
    {
        // Arrange
        var original = new RemoveMessage("device456");

        // Act
        var bytes = MessagePackSerializer.Serialize<IPCMessage>(original);
        var deserialized = MessagePackSerializer.Deserialize<IPCMessage>(bytes);

        // Assert
        Assert.IsType<RemoveMessage>(deserialized);
        var removeMsg = (RemoveMessage)deserialized;
        Assert.Equal("device456", removeMsg.deviceId);
        Assert.Equal("", removeMsg.reason);
    }

    [Fact]
    public void RemoveMessage_WildcardDeviceId_Serializes()
    {
        // Arrange
        var original = new RemoveMessage("*GHUB*", "rediscover_cleanup");

        // Act
        var bytes = MessagePackSerializer.Serialize<IPCMessage>(original);
        var deserialized = MessagePackSerializer.Deserialize<IPCMessage>(bytes);

        // Assert
        Assert.IsType<RemoveMessage>(deserialized);
        var removeMsg = (RemoveMessage)deserialized;
        Assert.Equal("*GHUB*", removeMsg.deviceId);
        Assert.Equal("rediscover_cleanup", removeMsg.reason);
    }
}
