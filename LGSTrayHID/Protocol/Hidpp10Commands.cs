namespace LGSTrayHID.Protocol
{
    /// <summary>
    /// Factory methods for HID++ 1.0 receiver commands (7-byte).
    /// HID++ 1.0 is used for receiver-level operations like device enumeration.
    /// These commands are sent to the receiver (device index 0xFF) rather than individual devices.
    /// </summary>
    public static class Hidpp10Commands
    {
        /// <summary>
        /// Create a command to query the receiver for the number of connected devices.
        /// Response byte[5] contains the device count.
        /// </summary>
        /// <returns>QueryDeviceCount command (HID++ 1.0)</returns>
        /// <example>
        /// // Before:
        /// // new byte[7] { 0x10, 0xFF, 0x81, 0x02, 0x00, 0x00, 0x00 }
        /// // After:
        /// var command = Hidpp10Commands.QueryDeviceCount();
        /// </example>
        public static byte[] QueryDeviceCount()
        {
            return new byte[7]
            {
                HidppVersion.MESSAGE_PREFIX,        // 0x10
                HidppDeviceIndex.RECEIVER,          // 0xFF (receiver)
                ReceiverCommand.QUERY_DEVICE_COUNT, // 0x81
                ReceiverCommand.SUB_COMMAND,        // 0x02
                0x00,
                0x00,
                0x00
            };
        }

        /// <summary>
        /// Create a command to force connected devices to announce themselves.
        /// Triggers device announcement messages (0x41) for all connected devices.
        /// Used during setup to discover devices that are already connected.
        /// </summary>
        /// <returns>ForceDeviceAnnounce command (HID++ 1.0)</returns>
        /// <example>
        /// // Before:
        /// // new byte[7] { 0x10, 0xFF, 0x80, 0x02, 0x02, 0x00, 0x00 }
        /// // After:
        /// var command = Hidpp10Commands.ForceDeviceAnnounce();
        /// </example>
        public static byte[] ForceDeviceAnnounce()
        {
            return new byte[7]
            {
                HidppVersion.MESSAGE_PREFIX,             // 0x10
                HidppDeviceIndex.RECEIVER,               // 0xFF (receiver)
                ReceiverCommand.FORCE_DEVICE_ANNOUNCE,   // 0x80
                ReceiverCommand.SUB_COMMAND,             // 0x02
                ReceiverCommand.ANNOUNCE_PARAM,          // 0x02
                0x00,
                0x00
            };
        }
    }
}
