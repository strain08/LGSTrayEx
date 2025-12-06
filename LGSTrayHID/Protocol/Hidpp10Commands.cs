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

        /// <summary>
        /// Create a command to enable battery reporting for a device (HID++ 1.0).
        /// Writes to the ENABLE_REPORTS register to enable automatic battery event broadcasts.
        /// This allows the device to send unsolicited battery updates when state changes.
        /// </summary>
        /// <param name="deviceIndex">Device index (1-6)</param>
        /// <returns>EnableBatteryReports command (HID++ 1.0)</returns>
        /// <remarks>
        /// Based on Linux kernel hid-logitech-hidpp.c implementation:
        /// hidpp10_set_register(hidpp_dev, HIDPP_REG_ENABLE_REPORTS, 0,
        ///     HIDPP_ENABLE_BAT_REPORT, HIDPP_ENABLE_BAT_REPORT);
        /// </remarks>
        /// <example>
        /// var command = Hidpp10Commands.EnableBatteryReports(1);
        /// await device.Parent.WriteRead10(device.Parent.DevShort, command, timeout: 1000);
        /// </example>
        public static byte[] EnableBatteryReports(byte deviceIndex)
        {
            return new byte[7]
            {
                HidppVersion.MESSAGE_PREFIX,                    // 0x10
                deviceIndex,                                     // 0x01-0x06
                (byte)(0x80 | Hidpp10Register.ENABLE_REPORTS),  // 0x80 = SET_REGISTER, 0x00 = register address
                0x00,                                            // Sub-address
                Hidpp10Register.ENABLE_BATTERY_REPORT,          // 0x04 = enable battery events
                Hidpp10Register.ENABLE_BATTERY_REPORT,          // Repeat for confirmation (per kernel driver)
                0x00
            };
        }
    }
}
