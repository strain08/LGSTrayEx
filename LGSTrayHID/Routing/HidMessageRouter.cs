using LGSTrayHID.Lifecycle;
using LGSTrayHID.Protocol;
using System.Threading.Channels;

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

        // Route 2b: unknown paired-slot device announcing itself (2.0-only bridge, e.g. G733 waking)
        if (await TryPickupBridgeDeviceAsync(message, buffer))
        {
            return;
        }

        // Route 3: Query responses (send to correlation channel)
        await _responseChannelWriter.WriteAsync(buffer);
    }

    /// <summary>
    /// Enumerates a device behind a 2.0-only bridge that announces itself via unsolicited traffic.
    /// Scoped to events (software id 0) from slots 1-6 with no device yet, so it never fires on
    /// command responses (our software id) or direct devices (index 0xFF).
    /// </summary>
    private async Task<bool> TryPickupBridgeDeviceAsync(Hidpp20 message, byte[] buffer)
    {
        if (message.GetSoftwareId() != 0) return false;
        byte idx = message.GetDeviceIdx();
        if (idx is < 1 or > 6) return false;
        if (_lifecycleManager.TryGetDevice(idx, out _)) return false;

        await _announcementHandler.HandleUnsolicitedTrafficAsync(idx, buffer);
        return true;
    }

    /// <summary>
    /// Attempts to route message as a device-specific event.
    /// </summary>
    /// <returns>True if message was handled by device, false otherwise</returns>
    private async Task<bool> TryRouteAsDeviceEventAsync(Hidpp20 message)
    {
        _lifecycleManager.TryGetDevice(message.GetDeviceIdx(), out HidppDevice? device);
        if (device is null) return false;        
        return await device.TryHandleBatteryEventAsync(message);      
    }
}
