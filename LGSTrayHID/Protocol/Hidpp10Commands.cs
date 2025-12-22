namespace LGSTrayHID.Protocol;

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

    /// <summary>
    /// Create a command to enable all report types for the receiver (HID++ 1.0).
    /// Writes to the ENABLE_REPORTS register to enable all notification types.
    /// This includes battery, wireless status, and other device events.
    /// </summary>
    /// <param name="deviceIndex">Device index (typically 0xFF for receiver-level)</param>
    /// <returns>EnableAllReports command (HID++ 1.0)</returns>
    /// <remarks>
    /// This is more comprehensive than EnableBatteryReports (0x04),
    /// enabling all available event types (0x0F = all bits set).
    /// Typically sent to receiver (0xFF) to enable device ON/OFF announcements.
    /// </remarks>
    /// <example>
    /// var command = Hidpp10Commands.EnableAllReports(0xFF);
    /// await device.Parent.WriteRead10(device.Parent.DevShort, command, timeout: 1000);
    /// </example>
    public static byte[] EnableAllReports(byte deviceIndex)
    {
        return new byte[7]
        {
            HidppVersion.MESSAGE_PREFIX,                    // 0x10
            deviceIndex,                                     // 0xFF (receiver) or 0x01-0x06
            (byte)(0x80 | Hidpp10Register.ENABLE_REPORTS),  // 0x80 = SET_REGISTER, 0x00 = register address
            0x00,                                            // Sub-address
            Hidpp10Register.ENABLE_ALL_REPORTS,             // 0x0F = enable all report types
            Hidpp10Register.ENABLE_ALL_REPORTS,             // Repeat for confirmation
            0x00
        };
    }

    /// <summary>
    /// Create a command to enable DJ (Device Juggler) notifications from the receiver.
    /// This activates real-time connection/disconnection events for wireless devices.
    /// Note: This is a DJ protocol command (0x20) not HID++ (0x10).
    /// </summary>
    /// <returns>EnableDJNotifications command (DJ protocol)</returns>
    /// <remarks>
    /// DJ notifications include:
    /// - 0x41: Device paired (connection)
    /// - 0x40: Device unpaired (disconnection)
    /// - 0x42: Connection status change (sleep/wake)
    ///
    /// Based on Linux kernel hid-logitech-dj.c implementation.
    /// </remarks>
    /// <example>
    /// var command = Hidpp10Commands.EnableDJNotifications();
    /// await devShort.WriteAsync(command);
    /// </example>
    public static byte[] EnableDJNotifications()
    {
        return new byte[7]
        {
            DJProtocol.REPORT_ID_SHORT,     // 0x20 (DJ SHORT, not HID++ 0x10)
            HidppDeviceIndex.RECEIVER,      // 0xFF (receiver)
            0x80,                           // Enable DJ notifications command
            0x00,                           // Padding
            0x00,                           // Padding
            0x00,                           // Padding
            0x00                            // Padding
        };
    }

    /// <summary>
    /// Create a command to query currently paired devices from the DJ receiver.
    /// This triggers NOTIF_DEVICE_PAIRED events for all connected devices.
    /// Should be sent after EnableDJNotifications().
    /// </summary>
    /// <returns>QueryPairedDevices command (DJ protocol)</returns>
    /// <remarks>
    /// The receiver will respond with a NOTIF_DEVICE_PAIRED (0x41) event
    /// for each currently paired device, allowing discovery of devices
    /// that were already connected when the application started.
    /// </remarks>
    public static byte[] QueryPairedDevices()
    {
        return new byte[7]
        {
            DJProtocol.REPORT_ID_SHORT,     // 0x20 (DJ SHORT, not HID++ 0x10)
            HidppDeviceIndex.RECEIVER,      // 0xFF (receiver)
            0x81,                           // Query paired devices command
            0x00,                           // Padding
            0x00,                           // Padding
            0x00,                           // Padding
            0x00                            // Padding
        };
    }
}
