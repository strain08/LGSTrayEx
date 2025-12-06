namespace LGSTrayHID.Protocol
{
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

    /// <summary>
    /// Convenience factory methods for common HID++ 2.0 command patterns.
    /// These methods provide pre-configured builders for frequently used commands.
    /// </summary>
    public static class Hidpp20Commands
    {
        /// <summary>
        /// Create a ping command (feature 0x0000, function 0x10).
        /// Used to test device responsiveness and for initialization retry logic.
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="payload">Unique payload byte for response matching</param>
        /// <returns>Ping command</returns>
        public static Hidpp20 Ping(byte deviceIndex, byte payload)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(0x00)  // Root feature
                .WithFunction(RootFunction.PING)
                .WithParams(0x00, 0x00, payload)
                .Build();
        }

        /// <summary>
        /// Create a command to get the feature index for a given feature ID (feature 0x0001, function 0x00).
        /// This is typically the first command sent to a device to discover feature support.
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="featureId">Feature ID to look up (e.g., 0x0001, 0x0005, 0x1000)</param>
        /// <returns>GetFeatureIndex command</returns>
        public static Hidpp20 GetFeatureIndex(byte deviceIndex, ushort featureId)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(0x00)  // Root feature - function 0x00 finds feature indices
                .WithFunction(FeatureSetFunction.GET_COUNT)
                .WithParams(0x00, (byte)(featureId >> 8), (byte)(featureId & 0xFF))
                .Build();
        }

        /// <summary>
        /// Create a command to get the total feature count from the device (feature 0x0001, function 0x00).
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="featureSetIndex">Feature index of the IFeatureSet feature (typically obtained via GetFeatureIndex)</param>
        /// <returns>GetFeatureCount command</returns>
        public static Hidpp20 GetFeatureCount(byte deviceIndex, byte featureSetIndex)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(featureSetIndex)
                .WithFunction(FeatureSetFunction.GET_COUNT)
                .Build();
        }

        /// <summary>
        /// Create a command to enumerate a feature at a specific index (feature 0x0001, function 0x10).
        /// This is used to iterate through all features supported by the device.
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="featureSetIndex">Feature index of the IFeatureSet feature</param>
        /// <param name="index">Feature index to query (0 to featureCount)</param>
        /// <returns>EnumerateFeature command</returns>
        public static Hidpp20 EnumerateFeature(byte deviceIndex, byte featureSetIndex, byte index)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(featureSetIndex)
                .WithFunction(FeatureSetFunction.GET_FEATURE_ID)
                .WithParams(index)
                .Build();
        }

        /// <summary>
        /// Create a command to get device name length (feature 0x0005, function 0x00).
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="deviceNameIndex">Feature index of the Device Name feature</param>
        /// <returns>GetDeviceNameLength command</returns>
        public static Hidpp20 GetDeviceNameLength(byte deviceIndex, byte deviceNameIndex)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(deviceNameIndex)
                .WithFunction(DeviceNameFunction.GET_NAME_LENGTH)
                .Build();
        }

        /// <summary>
        /// Create a command to get a chunk of the device name (feature 0x0005, function 0x10).
        /// Device names are read in 3-byte chunks.
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="deviceNameIndex">Feature index of the Device Name feature</param>
        /// <param name="offset">Byte offset to read from (typically incremented by 3)</param>
        /// <returns>GetDeviceNameChunk command</returns>
        public static Hidpp20 GetDeviceNameChunk(byte deviceIndex, byte deviceNameIndex, byte offset)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(deviceNameIndex)
                .WithFunction(DeviceNameFunction.GET_NAME_CHUNK)
                .WithParams(offset)
                .Build();
        }

        /// <summary>
        /// Create a command to get device type (feature 0x0005, function 0x20).
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="deviceNameIndex">Feature index of the Device Name feature</param>
        /// <returns>GetDeviceType command</returns>
        public static Hidpp20 GetDeviceType(byte deviceIndex, byte deviceNameIndex)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(deviceNameIndex)
                .WithFunction(DeviceNameFunction.GET_DEVICE_TYPE)
                .Build();
        }

        /// <summary>
        /// Create a command to get device firmware info (feature 0x0003, function 0x00).
        /// Returns unit ID, model ID, and serial number support flag.
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="fwInfoIndex">Feature index of the Device FW Info feature</param>
        /// <returns>GetDeviceFwInfo command</returns>
        public static Hidpp20 GetDeviceFwInfo(byte deviceIndex, byte fwInfoIndex)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(fwInfoIndex)
                .WithFunction(DeviceFwInfoFunction.GET_FW_INFO)
                .Build();
        }

        /// <summary>
        /// Create a command to get device serial number (feature 0x0003, function 0x20).
        /// Only works if the device reports serial number support.
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="fwInfoIndex">Feature index of the Device FW Info feature</param>
        /// <returns>GetSerialNumber command</returns>
        public static Hidpp20 GetSerialNumber(byte deviceIndex, byte fwInfoIndex)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(fwInfoIndex)
                .WithFunction(DeviceFwInfoFunction.GET_SERIAL_NUMBER)
                .Build();
        }

        /// <summary>
        /// Create a command to get battery status (features 0x1000/0x1001, function 0x00).
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="batteryFeatureIndex">Feature index of the battery feature</param>
        /// <returns>GetBatteryStatus command</returns>
        public static Hidpp20 GetBatteryStatus(byte deviceIndex, byte batteryFeatureIndex)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(batteryFeatureIndex)
                .WithFunction(BatteryFunction.GET_STATUS)
                .Build();
        }

        /// <summary>
        /// Create a command to get battery capability (feature 0x1004, function 0x10).
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <param name="batteryFeatureIndex">Feature index of the Unified Battery feature (0x1004)</param>
        /// <returns>GetBatteryCapability command</returns>
        public static Hidpp20 GetBatteryCapability(byte deviceIndex, byte batteryFeatureIndex)
        {
            return Hidpp20CommandBuilder.Create(deviceIndex)
                .WithFeatureIndex(batteryFeatureIndex)
                .WithFunction(BatteryFunction.GET_CAPABILITY)
                .Build();
        }
    }
}
