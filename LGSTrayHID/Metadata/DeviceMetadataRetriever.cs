using LGSTrayHID.Protocol;
using System.Text;

namespace LGSTrayHID.Metadata;

/// <summary>
/// Queries device metadata via HID++ protocol.
/// Consolidates all device information retrieval operations.
/// </summary>
public static class DeviceMetadataRetriever
{
    /// <summary>
    /// Retrieves the device name via HID++ Feature 0x0005.
    /// </summary>
    /// <param name="device">The HID++ device to query</param>
    /// <param name="featureIndex">Feature index for Device Name (0x0005)</param>
    /// <returns>Device name string</returns>
    public static async Task<string> GetDeviceNameAsync(HidppDevice device, byte featureIndex)
    {
        // Get device name length
        var ret = await device.Parent.WriteRead20(device.Parent.DevShort,
            Hidpp20Commands.GetDeviceNameLength(device.DeviceIdx, featureIndex));

        if (ret.Length == 0)
        {
            throw new InvalidOperationException("Failed to retrieve device name length (timeout)");
        }

        int nameLength = ret.GetParam(0);

        string name = "";

        // Read name in chunks (3 bytes at a time)
        while (name.Length < nameLength)
        {
            ret = await device.Parent.WriteRead20(device.Parent.DevShort,
                Hidpp20Commands.GetDeviceNameChunk(device.DeviceIdx, featureIndex, (byte)name.Length));

            if (ret.Length == 0)
            {
                throw new InvalidOperationException($"Failed to retrieve device name chunk at offset {name.Length} (timeout)");
            }

            name += Encoding.UTF8.GetString(ret.GetParams());
        }

        return name.TrimEnd('\0');
    }

    /// <summary>
    /// Retrieves the device type via HID++ Feature 0x0005.
    /// </summary>
    /// <param name="device">The HID++ device to query</param>
    /// <param name="featureIndex">Feature index for Device Name (0x0005)</param>
    /// <returns>Device type code (0=Keyboard, 3=Mouse, 8=Headset)</returns>
    public static async Task<int> GetDeviceTypeAsync(HidppDevice device, byte featureIndex)
    {
        var ret = await device.Parent.WriteRead20(device.Parent.DevShort,
            Hidpp20Commands.GetDeviceType(device.DeviceIdx, featureIndex));

        if (ret.Length == 0)
        {
            throw new InvalidOperationException("Failed to retrieve device type (timeout)");
        }

        return ret.GetParam(0);
    }

    /// <summary>
    /// Firmware info result containing unit ID, model ID, and serial number support.
    /// </summary>
    public record FirmwareInfo(string UnitId, string ModelId, bool SerialNumberSupported);

    /// <summary>
    /// Retrieves firmware info (unit ID, model ID, serial support) via HID++ Feature 0x0003.
    /// </summary>
    /// <param name="device">The HID++ device to query</param>
    /// <param name="featureIndex">Feature index for Device FW Info (0x0003)</param>
    /// <returns>Firmware information</returns>
    public static async Task<FirmwareInfo> GetFirmwareInfoAsync(HidppDevice device, byte featureIndex)
    {
        var ret = await device.Parent.WriteRead20(device.Parent.DevShort,
            Hidpp20Commands.GetDeviceFwInfo(device.DeviceIdx, featureIndex));

        if (ret.Length == 0 || ret.GetParams().Length < 15)
        {
            throw new InvalidOperationException("Failed to retrieve firmware info (timeout or insufficient data)");
        }

        string unitId = BitConverter.ToString(ret.GetParams().ToArray(), 1, 4).Replace("-", string.Empty);
        string modelId = BitConverter.ToString(ret.GetParams().ToArray(), 7, 5).Replace("-", string.Empty);
        bool serialNumberSupported = (ret.GetParam(14) & 0x1) == 0x1;

        return new FirmwareInfo(unitId, modelId, serialNumberSupported);
    }

    /// <summary>
    /// Retrieves the device serial number via HID++ Feature 0x0003.
    /// </summary>
    /// <param name="device">The HID++ device to query</param>
    /// <param name="featureIndex">Feature index for Device FW Info (0x0003)</param>
    /// <returns>Serial number string</returns>
    public static async Task<string> GetSerialNumberAsync(HidppDevice device, byte featureIndex)
    {
        var ret = await device.Parent.WriteRead20(device.Parent.DevShort,
            Hidpp20Commands.GetSerialNumber(device.DeviceIdx, featureIndex));

        if (ret.Length == 0 || ret.GetParams().Length < 11)
        {
            throw new InvalidOperationException("Failed to retrieve serial number (timeout or insufficient data)");
        }

        return BitConverter.ToString(ret.GetParams().ToArray(), 0, 11).Replace("-", string.Empty);
    }
}
