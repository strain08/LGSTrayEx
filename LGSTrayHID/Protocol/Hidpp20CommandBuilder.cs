namespace LGSTrayHID.Protocol;

/// <summary>
/// Fluent builder for constructing HID++ 2.0 commands (7-byte SHORT messages).
/// Provides a type-safe, self-documenting way to build commands instead of hardcoded byte arrays.
/// </summary>
/// <example>
/// var command = Hidpp20CommandBuilder.Create(deviceIndex: 1)
///     .WithFeatureIndex(0x05)
///     .WithFunction(DeviceNameFunction.GET_NAME_LENGTH)
///     .Build();
/// </example>
public readonly struct Hidpp20CommandBuilder
{
    private readonly byte[] _buffer;

    private Hidpp20CommandBuilder(byte[] buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// Start building a HID++ 2.0 command for the specified device.
    /// </summary>
    /// <param name="deviceIndex">Device index (1-6 for connected devices, 0xFF for receiver)</param>
    /// <returns>Builder instance for method chaining</returns>
    public static Hidpp20CommandBuilder Create(byte deviceIndex)
    {
        var buffer = new byte[7];
        buffer[0] = HidppVersion.MESSAGE_PREFIX;  // 0x10
        buffer[1] = deviceIndex;
        return new Hidpp20CommandBuilder(buffer);
    }

    /// <summary>
    /// Set the feature index (byte 2).
    /// This is typically obtained from the feature map during device initialization.
    /// </summary>
    /// <param name="featureIndex">Feature index (0x00-0xFF)</param>
    /// <returns>Builder instance for method chaining</returns>
    public Hidpp20CommandBuilder WithFeatureIndex(byte featureIndex)
    {
        _buffer[2] = featureIndex;
        return this;
    }

    /// <summary>
    /// Set the function ID (upper nibble of byte 3).
    /// The software ID is automatically set to the default value.
    /// </summary>
    /// <param name="functionId">Function ID (0x00-0x0F)</param>
    /// <param name="softwareId">Software ID for request correlation (default: 0x0A)</param>
    /// <returns>Builder instance for method chaining</returns>
    public Hidpp20CommandBuilder WithFunction(byte functionId, byte softwareId = HidppSoftwareId.DEFAULT)
    {
        _buffer[3] = (byte)((functionId << 4) | (softwareId & 0x0F));
        return this;
    }

    /// <summary>
    /// Set the command parameters (bytes 4-6).
    /// </summary>
    /// <param name="param0">First parameter byte (default: 0x00)</param>
    /// <param name="param1">Second parameter byte (default: 0x00)</param>
    /// <param name="param2">Third parameter byte (default: 0x00)</param>
    /// <returns>Builder instance for method chaining</returns>
    public Hidpp20CommandBuilder WithParams(byte param0 = 0, byte param1 = 0, byte param2 = 0)
    {
        _buffer[4] = param0;
        _buffer[5] = param1;
        _buffer[6] = param2;
        return this;
    }

    /// <summary>
    /// Build the final HID++ 2.0 command.
    /// </summary>
    /// <returns>Hidpp20 struct containing the command bytes</returns>
    public Hidpp20 Build()
    {
        return new Hidpp20(_buffer);
    }

    /// <summary>
    /// Implicit conversion to byte array for compatibility with existing APIs.
    /// </summary>
    public static implicit operator byte[](Hidpp20CommandBuilder builder)
    {
        return builder._buffer;
    }
}