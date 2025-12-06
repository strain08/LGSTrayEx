namespace LGSTrayHID.Protocol
{
    /// <summary>
    /// HID++ Protocol version markers
    /// </summary>
    public static class HidppVersion
    {
        /// <summary>HID++ 1.0 and 2.0 both use 0x10 as the message prefix</summary>
        public const byte MESSAGE_PREFIX = 0x10;
    }

    /// <summary>
    /// Special device indices used in HID++ protocol
    /// </summary>
    public static class HidppDeviceIndex
    {
        /// <summary>Receiver/broadcast address for HID++ 1.0 commands</summary>
        public const byte RECEIVER = 0xFF;

        // Note: Device indices 0x01-0x06 are assigned per connected device
    }

    /// <summary>
    /// HID++ feature identifiers (registered features in the HID++ specification)
    /// </summary>
    public static class HidppFeature
    {
        /// <summary>Root feature - used for ping operations</summary>
        public const ushort ROOT = 0x0000;

        /// <summary>IFeatureSet (0x0001) - Feature enumeration and discovery</summary>
        public const ushort FEATURE_SET = 0x0001;

        /// <summary>Device Firmware Information (0x0003) - Device ID, serial number, firmware version</summary>
        public const ushort DEVICE_FW_INFO = 0x0003;

        /// <summary>Device Name and Type (0x0005) - Device name string and device type</summary>
        public const ushort DEVICE_NAME = 0x0005;

        /// <summary>Battery Unified Level Status (0x1000) - Battery percentage and charging status</summary>
        public const ushort BATTERY_UNIFIED_LEVEL = 0x1000;

        /// <summary>Battery Voltage (0x1001) - Battery voltage with lookup table for percentage estimation</summary>
        public const ushort BATTERY_VOLTAGE = 0x1001;

        /// <summary>Unified Battery (0x1004) - Extended battery information</summary>
        public const ushort BATTERY_UNIFIED = 0x1004;
    }

    /// <summary>
    /// Function IDs for IFeatureSet feature (0x0001)
    /// Used to enumerate available features on a device
    /// </summary>
    public static class FeatureSetFunction
    {
        /// <summary>Get feature count from device</summary>
        public const byte GET_COUNT = 0x00;

        /// <summary>Get feature ID at given index</summary>
        public const byte GET_FEATURE_ID = 0x10;
    }

    /// <summary>
    /// Function IDs for Device Name feature (0x0005)
    /// </summary>
    public static class DeviceNameFunction
    {
        /// <summary>Get device name length in bytes</summary>
        public const byte GET_NAME_LENGTH = 0x00;

        /// <summary>Get device name chunk (3 bytes at a time)</summary>
        public const byte GET_NAME_CHUNK = 0x10;

        /// <summary>Get device type (keyboard, mouse, headset, etc.)</summary>
        public const byte GET_DEVICE_TYPE = 0x20;
    }

    /// <summary>
    /// Function IDs for Device Firmware Info feature (0x0003)
    /// </summary>
    public static class DeviceFwInfoFunction
    {
        /// <summary>Get firmware info (unit ID, model ID, serial support flag)</summary>
        public const byte GET_FW_INFO = 0x00;

        /// <summary>Get device serial number (if supported)</summary>
        public const byte GET_SERIAL_NUMBER = 0x20;
    }

    /// <summary>
    /// Function IDs for Root feature (0x0000)
    /// </summary>
    public static class RootFunction
    {
        /// <summary>Ping command - used to test device responsiveness</summary>
        public const byte PING = 0x10;
    }

    /// <summary>
    /// Function IDs for Battery features
    /// </summary>
    public static class BatteryFunction
    {
        /// <summary>Get battery status (used by features 0x1000 and 0x1001)</summary>
        public const byte GET_STATUS = 0x00;

        /// <summary>Get battery capability (used by feature 0x1004)</summary>
        public const byte GET_CAPABILITY = 0x10;
    }

    /// <summary>
    /// HID++ 1.0 receiver command codes
    /// </summary>
    public static class ReceiverCommand
    {
        /// <summary>Query number of connected devices on receiver</summary>
        public const byte QUERY_DEVICE_COUNT = 0x81;

        /// <summary>Force connected devices to announce themselves</summary>
        public const byte FORCE_DEVICE_ANNOUNCE = 0x80;

        /// <summary>Sub-command byte for receiver operations</summary>
        public const byte SUB_COMMAND = 0x02;

        /// <summary>Parameter for force announce operation</summary>
        public const byte ANNOUNCE_PARAM = 0x02;
    }

    /// <summary>
    /// Response indicators and special codes
    /// </summary>
    public static class HidppResponse
    {
        /// <summary>HID++ error response feature index</summary>
        public const byte ERROR = 0x8F;

        /// <summary>Device announcement message (hotplug arrival)</summary>
        public const byte DEVICE_ANNOUNCEMENT = 0x41;
    }

    /// <summary>
    /// Software ID used for request/response correlation
    /// </summary>
    public static class HidppSoftwareId
    {
        /// <summary>Default software ID used by this application (0x0A)</summary>
        public const byte DEFAULT = 0x0A;
    }

    /// <summary>
    /// Battery status codes from HID++ specification
    /// Used by features 0x1000 and 0x1004
    /// </summary>
    public static class BatteryStatusCode
    {
        /// <summary>Battery is discharging (not connected to power)</summary>
        public const byte DISCHARGING = 0;

        /// <summary>Battery is charging</summary>
        public const byte CHARGING = 1;

        /// <summary>Battery is charging slowly (low power charger)</summary>
        public const byte CHARGING_SLOW = 2;

        /// <summary>Battery is fully charged</summary>
        public const byte FULL = 3;

        /// <summary>Charging error detected</summary>
        public const byte CHARGING_ERROR = 4;

        // Feature 0x1001 uses different encoding with flags
        /// <summary>Charging flag bit for feature 0x1001</summary>
        public const byte CHARGING_FLAG = 0x80;

        /// <summary>Status mask for feature 0x1001</summary>
        public const byte STATUS_MASK = 0x07;
    }

    /// <summary>
    /// HID usage page and usage values for device detection
    /// </summary>
    public static class HidUsage
    {
        /// <summary>Vendor-specific usage page (Logitech HID++)</summary>
        public const ushort VENDOR_SPECIFIC_PAGE = 0xFF00;

        /// <summary>SHORT message type (7 bytes)</summary>
        public const ushort SHORT_MESSAGE = 0x0001;

        /// <summary>LONG message type (20 bytes)</summary>
        public const ushort LONG_MESSAGE = 0x0002;
    }

    /// <summary>
    /// Logitech vendor identifier
    /// </summary>
    public static class VendorId
    {
        /// <summary>Logitech vendor ID (0x046D)</summary>
        public const ushort LOGITECH = 0x046D;
    }
}
