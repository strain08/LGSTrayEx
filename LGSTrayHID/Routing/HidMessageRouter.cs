using LGSTrayHID.Lifecycle;
using LGSTrayHID.Protocol;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LGSTrayHID.Routing;

/// <summary>
/// Routes incoming HID++ messages to appropriate handlers.
/// Separates message type detection from device lifecycle and response correlation.
/// </summary>
public class HidMessageRouter
{
    private readonly DeviceAnnouncementHandler _announcementHandler;
    private readonly DeviceLifecycleManager _lifecycleManager;
    private readonly ChannelWriter<byte[]> _responseChannelWriter;

    public HidMessageRouter(
        DeviceAnnouncementHandler announcementHandler,
        DeviceLifecycleManager lifecycleManager,
        ChannelWriter<byte[]> responseChannelWriter)
    {
        _announcementHandler = announcementHandler;
        _lifecycleManager = lifecycleManager;
        _responseChannelWriter = responseChannelWriter;
    }

    /// <summary>
    /// Routes incoming HID++ message to the appropriate handler.
    /// Routing order:
    ///   1. Device announcements (ON/OFF events) → DeviceAnnouncementHandler
    ///   2. Device-specific events (wireless status, battery) → Device handlers
    ///   3. Query responses → Response correlation channel
    /// </summary>
    /// <param name="buffer">Raw HID++ message buffer</param>
    public async Task RouteMessageAsync(byte[] buffer)
    {
        Hidpp20 message = buffer;

        // Route 1: Device announcements (ON/OFF events)
        if (message.IsDeviceAnnouncement())
        {
            await _announcementHandler.HandleAnnouncementAsync(buffer);
            return;
        }

        // Route 2: Device-specific events (wireless status, battery events)
        if (await TryRouteAsDeviceEventAsync(message))
        {
            return; // Event handled
        }

        // Route 3: Query responses (send to correlation channel)
        await _responseChannelWriter.WriteAsync(buffer);
    }

    /// <summary>
    /// Attempts to route message as a device-specific event.
    /// Tries wireless status event (0x1D4B) first, then battery event.
    /// </summary>
    /// <returns>True if message was handled by device, false otherwise</returns>
    private async Task<bool> TryRouteAsDeviceEventAsync(Hidpp20 message)
    {
        byte deviceIdx = message.GetDeviceIdx();
        
        if (!_lifecycleManager.TryGetDevice(deviceIdx, out HidppDevice? device))
        {
            return false; // Device not found
        }
        
        if (device is null)
        {
            return false; // Device not found
        }

        // Try battery event (features 0x1000, 0x1001, 0x1004)
        if (await device.TryHandleBatteryEventAsync(message))
        {
            return true;
        }

        return false; // Not a device event
    }
}
