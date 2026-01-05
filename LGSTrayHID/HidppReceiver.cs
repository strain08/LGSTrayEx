using LGSTrayHID.HidApi;
using LGSTrayHID.Initialization;
using LGSTrayHID.Lifecycle;
using LGSTrayHID.Protocol;
using LGSTrayHID.Routing;
using LGSTrayPrimitives;
using LGSTrayPrimitives.Retry;
using System.Threading.Channels;

namespace LGSTrayHID;

public class HidppReceiver : IDisposable
{
    public HidDevicePtr DevShort { get; private set; } = IntPtr.Zero;
    public HidDevicePtr DevLong { get; private set; } = IntPtr.Zero;
    public IReadOnlyDictionary<ushort, HidppDevice> DeviceCollection => _lifecycleManager.Devices;

    private int _pingPayloadCounter = 0x55;

    private readonly DeviceLifecycleManager _lifecycleManager;
    private readonly DeviceAnnouncementHandler _announcementHandler;
    private readonly HidMessageRouter _messageRouter;
    private readonly HidMessageChannel _messageChannel;
    private readonly DeviceEnumerator _enumerator;

    private readonly SemaphoreSlim _correlationSemaphore = new(1, 1); // Ensure sequential command-response correlation
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

    public HidppReceiver(bool keepPollingWithEvents, int batteryEventDelaySeconds)
    {
        _lifecycleManager = new DeviceLifecycleManager(this, keepPollingWithEvents, batteryEventDelaySeconds);
        _correlator = new CommandResponseCorrelator(_correlationSemaphore, _channel.Reader);
        _announcementHandler = new DeviceAnnouncementHandler(_lifecycleManager, _initSemaphore);
        _messageRouter = new HidMessageRouter(_announcementHandler, _lifecycleManager, _channel.Writer);
        _messageChannel = new HidMessageChannel(_messageRouter);
        _enumerator = new DeviceEnumerator(this, _lifecycleManager);
    }


    public async Task SetUp(HidppMessageType messageType, nint dev, bool isWiredModeDevice = false)
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
        await _messageChannel.WaitUntilReadyAsync();
        DiagnosticLogger.Log("HID read threads ready");

        // Detect device type (receiver vs direct device)
        bool isReceiver = await TryDetectReceiverAsync();

        if (isReceiver)
        {
            DiagnosticLogger.Log("Initializing as RECEIVER device");

            // Note: DJ protocol (0x20/0x21) does not work with BOLT receivers.
            // BOLT uses HID++ 2.0 Feature 0x1D4B (Wireless Device Status) for connection events.
            // Events are automatically enabled by the battery enable command (HID++ 1.0 register 0x00).

            // Enable receiver notifications for device on/off events
            await EnableReceiverNotificationsAsync();

            // Enumerate devices (query + announce, fallback ping)
            await _enumerator.EnumerateDevicesAsync();
        }
        else
        {
            DiagnosticLogger.Log($"Initializing as DIRECT device{(isWiredModeDevice ? " (WIRED MODE - fast-track)" : "")}");

            // Initialize single device at index 0xFF
            await InitializeDirectDeviceAsync(isWiredModeDevice);
        }
    }

    /// <summary>
    /// Detects whether this HID device is a receiver or a direct device.
    /// Sends QueryDeviceCount command - receivers respond, direct devices timeout.
    /// </summary>
    /// <returns>True if receiver detected, false if direct device</returns>
    private async Task<bool> TryDetectReceiverAsync()
    {
        try
        {
            DiagnosticLogger.Log("Detecting device type (receiver vs direct device)...");

            // Query receiver for device count with short timeout
            // Direct devices won't respond to this HID++ 1.0 receiver command
            byte[] response = await WriteRead10(
                DevShort,
                Hidpp10Commands.QueryDeviceCount(),
                timeout: 500  // Short timeout for fast detection
            );

            // Check if we got a valid response
            if (response.Length >= 6 &&
                response[2] == ReceiverCommand.QUERY_DEVICE_COUNT &&
                response[3] == ReceiverCommand.SUB_COMMAND)
            {
                byte deviceCount = response[5];
                DiagnosticLogger.Log($"Receiver detected - reports {deviceCount} connected devices");
                return true; // RECEIVER MODE
            }

            // Empty response or invalid format - likely direct device
            DiagnosticLogger.Log("No valid receiver response - treating as direct device");
            return false; // DIRECT DEVICE MODE
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log($"Receiver detection failed: {ex.Message} - treating as direct device");
            return false; // DIRECT DEVICE MODE (safe default)
        }
    }

    /// <summary>
    /// Sends a HID++ 1.0 command to the specified HID device and waits asynchronously for a matching response.
    /// </summary>
    /// <remarks>The method matches responses based on the command and sub-address fields in the buffer. For
    /// SET_REGISTER commands (0x80), it expects a GET_REGISTER (0x81) response with the same sub-address.
    /// <br>The method throws an exception if the object has been disposed.</br>
    /// </remarks>
    /// <param name="hidDevicePtr">A pointer to the HID device to which the command is sent.</param>
    /// <param name="buffer">The byte array containing the HID++ 1.0 command to send. Cannot be null.</param>
    /// <param name="timeout">The maximum time, in milliseconds, to wait for a response before the operation times out. The default is 100
    /// milliseconds. Must be greater than zero.</param>
    /// <param name="backoffStrategy">Optional backoff strategy for retry logic with exponential backoff. If null, executes single attempt.</param>
    /// <param name="cancellationToken">Cancellation token for retry operations.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the response bytes received from the
    /// device. The returned array may be empty if no response is received within the specified timeout.</returns>
    public async Task<byte[]> WriteRead10(
        HidDevicePtr hidDevicePtr,
        byte[] buffer,
        int timeout = 100,
        BackoffStrategy? backoffStrategy = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

        // If no backoff strategy provided, execute single attempt with specified timeout
        if (backoffStrategy == null)
        {
            return await _correlator.SendHidpp10AndWaitAsync(
                hidDevicePtr,
                buffer,
                matcher: response => (response[2] == buffer[2] && response[3] == buffer[3]) ||
                                     (buffer[2] == 0x80 && response[2] == 0x81 && response[3] == buffer[3]), // SET_REGISTER (0x80) → GET_REGISTER (0x81), same sub-address
                timeout: timeout,
                earlyExit: null
            );
        }

        // Execute with retry logic using backoff strategy
        byte[]? result = null;
        await foreach (var attempt in backoffStrategy.GetAttemptsAsync(cancellationToken))
        {
            // Apply delay before retry attempts (not on first attempt)
            if (attempt.AttemptNumber > 1)
            {
                DiagnosticLogger.Log($"[Receiver] //{backoffStrategy.ProfileName}// HID++ 1.0 retry attempt {attempt.AttemptNumber} " +
                                     $"with timeout {attempt.Timeout.TotalMilliseconds}ms " +
                                     $"after delay {attempt.Delay.TotalMilliseconds}ms");
                await Task.Delay(attempt.Delay, cancellationToken);
            }

            result = await _correlator.SendHidpp10AndWaitAsync(
                hidDevicePtr,
                buffer,
                matcher: response => (response[2] == buffer[2] && response[3] == buffer[3]) ||
                                     (buffer[2] == 0x80 && response[2] == 0x81 && response[3] == buffer[3]),
                timeout: (int)attempt.Timeout.TotalMilliseconds,
                earlyExit: null
            );

            // Success - got valid response
            if (result?.Length > 0)
            {
                break;
            }
        }

        // Return result (empty if all retries failed)
        return result ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Sends a HID++ 2.0 request to the specified HID device and asynchronously waits for the corresponding response.
    /// </summary>
    /// <remarks>The response is matched based on feature index, device index, and software ID. If the device
    /// does not respond within the specified timeout, the operation will fail. This method throws an exception if the
    /// object has been disposed.</remarks>
    /// <param name="hidDevicePtr">A pointer to the HID device to which the request is sent.</param>
    /// <param name="buffer">The HID++ 2.0 message to send to the device. Must not be null.</param>
    /// <param name="timeout">The maximum time, in milliseconds, to wait for a response before the operation times out. The default is 100
    /// milliseconds.</param>
    /// <param name="ignoreHID10">If <see langword="true"/>, HID++ 1.0 error responses are ignored; otherwise, the operation completes early if
    /// such an error is received. The default is <see langword="true"/>.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the HID++ 2.0 response message from
    /// the device.</returns>
    public async Task<Hidpp20> WriteRead20(
        HidDevicePtr hidDevicePtr,
        Hidpp20 buffer,
        int timeout = 100,
        bool ignoreHID10 = true,
        BackoffStrategy? backoffStrategy = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

        // If no backoff strategy provided, execute single attempt with specified timeout
        if (backoffStrategy == null)
        {
            return await _correlator.SendHidpp20AndWaitAsync(
                hidDevicePtr,
                buffer,
                matcher: response => (response.GetFeatureIndex() == buffer.GetFeatureIndex()) &&
                                     (response.GetDeviceIdx() == buffer.GetDeviceIdx()) &&
                                     (response.GetSoftwareId() == HidppSoftwareId.DEFAULT),
                timeout: timeout,
                earlyExit: ignoreHID10 ? null : response => response.IsError()
            );
        }        
        // Execute with retry logic using backoff strategy
        Hidpp20? result = null;
        await foreach (var attempt in backoffStrategy.GetAttemptsAsync(cancellationToken))
        {
            if (attempt.AttemptNumber > 1)
            {                
                DiagnosticLogger.Log($"[{_lifecycleManager.GetDeviceName(buffer.GetDeviceIdx())}] //{backoffStrategy.ProfileName}// retry attempt {attempt.AttemptNumber} " +                                     
                                     $"with timeout {attempt.Timeout.TotalMilliseconds} " +
                                     $"after delay {attempt.Delay.TotalMilliseconds} ms");
                await Task.Delay(attempt.Delay, cancellationToken);
            }

            result = await _correlator.SendHidpp20AndWaitAsync(
                hidDevicePtr,
                buffer,
                matcher: response => (response.GetFeatureIndex() == buffer.GetFeatureIndex()) &&
                                     (response.GetDeviceIdx() == buffer.GetDeviceIdx()) &&
                                     (response.GetSoftwareId() == HidppSoftwareId.DEFAULT),
                timeout: (int)attempt.Timeout.TotalMilliseconds,
                earlyExit: ignoreHID10 ? null : response => response.IsError()
            );

            // Success - got valid response
            if (result?.Length > 0)
            {
                break;
            }
        }

        // Return result (empty if all retries failed)
        return result ?? new Hidpp20();
    }

    public async Task<bool> Ping20(byte deviceId, int timeout = 100, bool ignoreHIDPP10 = true)
    {
        ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

        // Thread-safe increment with wrap-around to byte range
        byte pingPayload = (byte)Interlocked.Increment(ref _pingPayloadCounter);
        Hidpp20 command = Hidpp20Commands.Ping(deviceId, pingPayload);

        Hidpp20 ret = await _correlator.SendHidpp20AndWaitAsync(
            DevShort,
            command,
            matcher: response => (response.GetFeatureIndex() == 0x00) &&
                                 (response.GetDeviceIdx() == deviceId) &&
                                 (response.GetSoftwareId() == HidppSoftwareId.DEFAULT) &&
                                 (response.GetParam(2) == pingPayload),
            timeout: timeout,
            earlyExit: ignoreHIDPP10 ? null : response => response.IsError()
        );

        return ret.Length > 0;
    }

    /// <summary>
    /// Pings a device until achieving the required number of consecutive successful pings.
    /// Uses exponential backoff between retry attempts with progressive timeout.
    /// </summary>
    /// <param name="deviceId">The device index to ping (1-6 for receiver slots)</param>
    /// <param name="successThreshold">Number of consecutive successful pings required (default: 3)</param>
    /// <param name="maxPingsPerAttempt">Maximum ping attempts per retry iteration (default: 10)</param>
    /// <param name="backoffStrategy">Backoff strategy for retry delays and progressive timeout</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the device achieved consecutive successes; false if all retries exhausted</returns>
    public async Task<bool> PingUntilConsecutiveSuccess(
        byte deviceId,
        int successThreshold = 3,
        int maxPingsPerAttempt = 10,
        bool ignoreHIDPP10 = true,
        BackoffStrategy? backoffStrategy = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

        // If no backoff strategy provided, do single attempt with default timeout
        if (backoffStrategy == null)
        {
            int consecutiveSuccesses = 0;
            for (int i = 0; i < maxPingsPerAttempt; i++)
            {
                bool success = await Ping20(deviceId, timeout: 100, ignoreHIDPP10);
                consecutiveSuccesses = success ? consecutiveSuccesses + 1 : 0;

                if (consecutiveSuccesses >= successThreshold)
                    return true;
            }
            return false;
        }

        // Execute with retry logic using backoff strategy
        await foreach (var attempt in backoffStrategy.GetAttemptsAsync(cancellationToken))
        {
            // Add delay before retry attempts (not on first attempt)
            if (attempt.AttemptNumber > 1)
            {
                await Task.Delay(attempt.Delay, cancellationToken);
            }

            // Try to achieve consecutive successful pings with progressive timeout
            int consecutiveSuccesses = 0;
            for (int i = 0; i < maxPingsPerAttempt; i++)
            {
                bool success = await Ping20(deviceId, timeout: (int)attempt.Timeout.TotalMilliseconds);
                consecutiveSuccesses = success ? consecutiveSuccesses + 1 : 0;

                // Success - achieved required consecutive pings
                if (consecutiveSuccesses >= successThreshold)
                {
                    return true;
                }
            }

            // Failed this attempt - will retry with longer delay/timeout
        }

        // All retry attempts exhausted
        return false;
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
            byte[] ret = await WriteRead10(
                DevShort,
                enableAllReports,
                backoffStrategy: GlobalSettings.ReceiverInitBackoff);
            DiagnosticLogger.Log($"Receiver EnableAllReports response: {BitConverter.ToString(ret)}");
            if (ret.Length == 0)
            {
                throw new Exception("Received empty array after retry.");
            }
            DiagnosticLogger.Log("Receiver device on/off notifications enabled");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log($"Failed to enable receiver notifications: {ex.Message}");
            // Non-fatal - continue with standard initialization
        }
    }

    /// <summary>
    /// Initializes a direct HID++ device (not connected through a receiver).
    /// Direct devices use device index 0xFF (receiver broadcast address).
    /// Examples: G515 keyboard in wired mode, G PRO X SUPERLIGHT wired.
    /// </summary>
    /// <param name="isWiredModeDevice">True if this is a wired mode device (enables fast-track init)</param>
    private async Task InitializeDirectDeviceAsync(bool isWiredModeDevice = false)
    {
        const byte DIRECT_DEVICE_INDEX = 0xFF;

        try
        {
            string initMode = isWiredModeDevice ? "WIRED MODE (fast-track)" : "standard";
            DiagnosticLogger.Log($"Starting direct device initialization at index 0xFF ({initMode})");

            // Create device at index 0xFF (direct device uses receiver address)
            var device = _lifecycleManager.CreateDevice(DIRECT_DEVICE_INDEX, isWiredModeDevice);

            // Initialize device (ping test, feature enumeration, battery setup)
            // This reuses the exact same initialization path as receiver devices
            await device.InitAsync();

            DiagnosticLogger.Log($"Direct device initialization complete - {device.Identifier} ({device.DeviceName})");
        }
        catch (Exception ex)
        {
            var stackFirstLine = ex.StackTrace?.Split('\n')[0].Trim() ?? "No stack trace";
            DiagnosticLogger.LogError($"Direct device initialization failed: " +
                                     $"{ex.GetType().Name} - {ex.Message} | {stackFirstLine}");
            // Non-fatal - device may not be HID++ compatible or may be in sleep mode
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
                _correlationSemaphore.Dispose();
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
