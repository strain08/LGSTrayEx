using LGSTrayHID.HidApi;
using LGSTrayHID.Initialization;
using LGSTrayHID.Lifecycle;
using LGSTrayHID.Protocol;
using LGSTrayHID.Routing;
using LGSTrayPrimitives;
using System.Data;
using System.Threading.Channels;

namespace LGSTrayHID;

public class HidppReceiver : IDisposable
{
    public HidDevicePtr DevShort { get; private set; } = IntPtr.Zero;
    public HidDevicePtr DevLong { get; private set; } = IntPtr.Zero;
    public IReadOnlyDictionary<ushort, HidppDevice> DeviceCollection => _lifecycleManager.Devices;
    public const byte SW_ID = 0x0A;
    private byte PING_PAYLOAD = 0x55;

    private readonly DeviceLifecycleManager _lifecycleManager;
    private readonly DeviceAnnouncementHandler _announcementHandler;
    private readonly HidMessageRouter _messageRouter;
    private readonly HidMessageChannel _messageChannel;
    private readonly DeviceEnumerator _enumerator;

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly SemaphoreSlim _initSemaphore = new(1, 1); // Ensure sequential device initialization
    private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(5)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false, // Fix: 2 writers (SHORT + LONG read threads)
    });
    private readonly CommandResponseCorrelator _correlator;
    

    private int _disposeCount = 0;
    public bool Disposed => _disposeCount > 0;

    public HidppReceiver()
    {
        _lifecycleManager = new DeviceLifecycleManager(this);
        _correlator = new CommandResponseCorrelator(_semaphore, _channel.Reader);
        _announcementHandler = new DeviceAnnouncementHandler(_lifecycleManager, _initSemaphore);
        _messageRouter = new HidMessageRouter(_announcementHandler, _lifecycleManager, _channel.Writer);
        _messageChannel = new HidMessageChannel(_messageRouter);
        _enumerator = new DeviceEnumerator(this, _lifecycleManager);
    }

    
    public async Task SetUp(HidppMessageType messageType, nint dev)
    {            
        switch (messageType)
        {
            case HidppMessageType.SHORT:
                DevShort = dev;
                break;
            case HidppMessageType.LONG:
                DevLong = dev;
                break;
        }

        if ((DevShort == IntPtr.Zero) || (DevLong == IntPtr.Zero)) return;

        // Start reading threads
        _messageChannel.StartReading(DevShort, DevLong);

        // Wait for read threads to be ready before sending commands
        await Task.Delay(500);

        // Note: DJ protocol (0x20/0x21) does not work with BOLT receivers.
        // BOLT uses HID++ 2.0 Feature 0x1D4B (Wireless Device Status) for connection events.
        // Events are automatically enabled by the battery enable command (HID++ 1.0 register 0x00).

        // Enable receiver notifications for device on/off events
        await EnableReceiverNotificationsAsync();

        // Enumerate devices (query + announce, fallback ping)
        await _enumerator.EnumerateDevicesAsync();
    }

    public async Task<byte[]> WriteRead10(HidDevicePtr hidDevicePtr, byte[] buffer, int timeout = 100)
    {
        ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

        return await _correlator.SendHidpp10AndWaitAsync(
            hidDevicePtr,
            buffer,
            matcher: response => (response[2] == 0x8F) || (response[2] == buffer[2]),
            timeout: timeout,
            earlyExit: null
        );
    }

    public async Task<Hidpp20> WriteRead20(HidDevicePtr hidDevicePtr, Hidpp20 buffer, int timeout = 100, bool ignoreHID10 = true)
    {
        ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

        return await _correlator.SendHidpp20AndWaitAsync(
            hidDevicePtr,
            buffer,
            matcher: response => (response.GetFeatureIndex() == buffer.GetFeatureIndex()) &&
                                 (response.GetSoftwareId() == SW_ID),
            timeout: timeout,
            earlyExit: ignoreHID10 ? null : response => response.IsError()
        );
    }

    public async Task<bool> Ping20(byte deviceId, int timeout = 100, bool ignoreHIDPP10 = true)
    {
        ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

        byte pingPayload = ++PING_PAYLOAD;
        Hidpp20 command = Hidpp20Commands.Ping(deviceId, pingPayload);

        Hidpp20 ret = await _correlator.SendHidpp20AndWaitAsync(
            DevShort,
            command,
            matcher: response => (response.GetFeatureIndex() == 0x00) &&
                                (response.GetSoftwareId() == SW_ID) &&
                                (response.GetParam(2) == pingPayload),
            timeout: timeout,
            earlyExit: ignoreHIDPP10 ? null : response => response.IsError()
        );

        return ret.Length > 0;
    }

    /// <summary>
    /// Enables receiver-level notifications for device connection/disconnection events.
    /// Sends EnableBatteryReports (0x00) and EnableAllReports (0x0F) to receiver (0xFF).
    /// Non-fatal - continues initialization if this fails.
    /// </summary>
    private async Task EnableReceiverNotificationsAsync()
    {
        try
        {
            // This enables the receiver to send 0x41 announcements when devices turn on/off
            // Same command as per-device battery enable, but sent to receiver index 0xFF
          //  var enableReceiverNotifications = Hidpp10Commands.EnableBatteryReports(0xFF);
         //   byte[] ret = await WriteRead10(DevShort, enableReceiverNotifications, 1000);
          //  DiagnosticLogger.Log($"Receiver EnableBatteryReports response: {BitConverter.ToString(ret)}");

            // Also try enabling all notification types (battery + wireless + others)
            byte[] enableAllReports = Hidpp10Commands.EnableAllReports(0xFF);
            byte[] ret = await WriteRead10(DevShort, enableAllReports, 1000);
            DiagnosticLogger.Log($"Receiver EnableAllReports response: {BitConverter.ToString(ret)}");

            DiagnosticLogger.Log("Receiver device on/off notifications enabled");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log($"Failed to enable receiver notifications: {ex.Message}");
            // Non-fatal - continue with standard initialization
        }
    }
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Increment(ref _disposeCount) == 1)
        {
            DiagnosticLogger.Log($"HidppReceiver.Dispose starting - Device count: {_lifecycleManager.Count}");

            if (disposing)
            {
                // Dispose devices first (stops battery polling tasks)
                _lifecycleManager.DisposeAll();

                // Then dispose message channel (stops read threads)
                _messageChannel.Dispose();

                // Dispose synchronization primitives
                _semaphore.Dispose();
                _initSemaphore.Dispose();

                // Note: _correlator doesn't own _semaphore or _channel, so we don't dispose it
                // Note: Channel<T> doesn't implement IDisposable - no cleanup needed
            }

            // Clear device handles
            DevShort = IntPtr.Zero;
            DevLong = IntPtr.Zero;

            DiagnosticLogger.Log("HidppReceiver.Dispose completed");
        }
    }

    ~HidppReceiver()
    {
        Dispose(disposing: false);
    }

}
