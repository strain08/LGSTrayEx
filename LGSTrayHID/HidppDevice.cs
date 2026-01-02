using LGSTrayHID.Battery;
using LGSTrayHID.Features;
using LGSTrayHID.Metadata;
using LGSTrayHID.Protocol;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;


namespace LGSTrayHID;

public class HidppDevice : IDisposable
{
    public HidppReceiver Parent { get; init; }
    public byte DeviceIdx { get; init; }
    public string DeviceName { get; private set; } = string.Empty;
    public int DeviceType { get; private set; } = 3; //device type 3 = mouse
    public string Identifier { get; private set; } = string.Empty;
    public Dictionary<ushort, byte> FeatureMap { get; private set; } = [];


    private IBatteryFeature? _batteryFeature;
    private DateTimeOffset lastUpdate = DateTimeOffset.MinValue;
    private int _consecutivePollFailures = 0;

    // Battery event tracking
    private byte _batteryFeatureIndex = 0xFF; // 0xFF = not set
    private readonly BatteryEventThrottler _eventThrottler = new(500); // 500ms throttle window
    private readonly BatteryUpdatePublisher _batteryPublisher = new(); // Handles deduplication and IPC
    private bool _forceNextUpdate = false; // Force next battery update to bypass deduplication

    // Configuration settings
    private readonly bool _keepPollingWithEvents;
    private readonly int _batteryEventDelaySeconds;
    private DateTimeOffset _deviceOnTime = DateTimeOffset.MinValue;

    // Semaphore to prevent concurrent InitAsync calls
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    // Disposal and cancellation support
    private readonly CancellationTokenSource _cancellationSource = new();

    private Task? _pollingTask;
    private readonly CancellationTokenSource _poolingCts = new();
    public void CancelPooling() => _poolingCts.Cancel();

    // Track device online/offline state (independent of polling state)
    private bool _isOnline = false;
    public bool IsOnline => _isOnline;
    public void SetOffline() => _isOnline = false;

    private int _disposeCount = 0;
    public bool Disposed => _disposeCount > 0;

    public HidppDevice(HidppReceiver parent, byte deviceIdx, bool keepPollingWithEvents = false, int batteryEventDelaySeconds = 0)
    {
        Parent = parent;
        DeviceIdx = deviceIdx;
        _keepPollingWithEvents = keepPollingWithEvents;
        _batteryEventDelaySeconds = batteryEventDelaySeconds;
    }

    public async Task InitAsync()
    {
        await _initSemaphore.WaitAsync();
        try
        {
            Hidpp20 ret;

            // Sync Ping with retry logic for sleeping devices
            // Requires 3 consecutive successful pings with exponential backoff
            DiagnosticLogger.Log($"Starting ping test for HID device index {DeviceIdx}");

            bool pingSuccess = await Parent.PingUntilConsecutiveSuccess(
                deviceId: DeviceIdx,
                successThreshold: 3,
                maxPingsPerAttempt: 10,
                backoffStrategy: GlobalSettings.InitBackoff,
                cancellationToken: _cancellationSource.Token);

            if (!pingSuccess)
            {
                DiagnosticLogger.LogWarning($"HID device index {DeviceIdx} failed ping test after {GlobalSettings.InitBackoff.MaxAttempts} attempts");
                return;
            }

            DiagnosticLogger.Log($"HID device index {DeviceIdx} passed ping test");

            // Find IFeatureSet (0x0001) - get its feature index
            ret = await Parent.WriteRead20(Parent.DevShort,
                                           Hidpp20Commands.GetFeatureIndex(DeviceIdx, HidppFeature.FEATURE_SET),                                           
                                           backoffStrategy: GlobalSettings.FeatureEnumBackoff);
            FeatureMap[HidppFeature.FEATURE_SET] = ret.GetParam(0);

            // Get Feature Count
            ret = await Parent.WriteRead20(Parent.DevShort,
                                           Hidpp20Commands.GetFeatureCount(DeviceIdx, FeatureMap[HidppFeature.FEATURE_SET]),
                                           backoffStrategy: GlobalSettings.FeatureEnumBackoff,
                                           cancellationToken: _cancellationSource.Token);
            int featureCount = ret.GetParam(0);

            // Enumerate Features with retry logic
            for (byte i = 0; i <= featureCount; i++)
            {
                // Query feature with retry (backoff strategy handles retries)
                ret = await Parent.WriteRead20(Parent.DevShort,
                                               Hidpp20Commands.EnumerateFeature(DeviceIdx, FeatureMap[HidppFeature.FEATURE_SET], i),
                                               backoffStrategy: GlobalSettings.FeatureEnumBackoff,
                                               cancellationToken: _cancellationSource.Token);

                // Check if we got a valid response after all retries
                if (ret.Length == 0)
                {
                    DiagnosticLogger.LogWarning($"[Device {DeviceIdx}] Feature enumeration timeout at index {i} " +
                                                $"after {GlobalSettings.FeatureEnumBackoff.MaxAttempts} attempts, " +
                                                $"stopping enumeration");
                    break;
                }

                ushort featureId = ret.GetFeatureId();

                FeatureMap[featureId] = i;

                // Log feature mapping for debugging connection events
            //    DiagnosticLogger.Log($"[Device {DeviceIdx}] Feature 0x{featureId:X04} mapped to index {i}");
            }

            await InitPopulateAsync();
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    private async Task InitPopulateAsync()
    {

        DiagnosticLogger.Log($"Enumerating features for HID device index {DeviceIdx}");

        // Device name
        if (FeatureMap.TryGetValue(HidppFeature.DEVICE_NAME, out byte featureId))
        {
            DeviceName = await DeviceMetadataRetriever.GetDeviceNameAsync(this, featureId);

            // Check if device is filtered in settings
            if (!DeviceFilterValidator.IsDeviceAllowed(DeviceName, GlobalSettings.settings.DisabledDevices, out string? matchedPattern))
            {
                DiagnosticLogger.LogWarning($"HID device '{DeviceName}' filtered by disabledDevices config (matched: '{matchedPattern}')");
                return;
            }

            DeviceType = await DeviceMetadataRetriever.GetDeviceTypeAsync(this, featureId);
        }
        else
        {
            // Device does not have a name/Hidpp error ignore it
            DiagnosticLogger.LogWarning($"HID device index {DeviceIdx} missing feature 0x0005 (device name), ignoring");
            return;
        }

        // Device identifier
        if (FeatureMap.TryGetValue(HidppFeature.DEVICE_FW_INFO, out featureId))
        {
            var fwInfo = await DeviceMetadataRetriever.GetFirmwareInfoAsync(this, featureId);

            string? serialNumber = null;
            if (fwInfo.SerialNumberSupported)
            {
                serialNumber = await DeviceMetadataRetriever.GetSerialNumberAsync(this, featureId);
                DiagnosticLogger.Log($"[{DeviceName}] Serial Number: {serialNumber}");
            }
            else
            {
                DiagnosticLogger.Log($"[{DeviceName}] Serial Number not supported by device firmware");
                DiagnosticLogger.Log($"[{DeviceName}] UnitId: {fwInfo.UnitId} ModelId: {fwInfo.ModelId}");
            }


            Identifier = DeviceIdentifierGenerator.GenerateIdentifier(serialNumber, fwInfo.UnitId, fwInfo.ModelId, DeviceName);
        }
        else
        {
            // Device does not have firmware info - use device name hash as identifier
            Identifier = DeviceIdentifierGenerator.GenerateIdentifier(null, null, null, DeviceName);
        }

        // Select battery feature using factory pattern
        _batteryFeature = BatteryFeatureFactory.GetBatteryFeature(FeatureMap);

        // Log battery feature presence and enable events
        if (_batteryFeature != null)
        {
            DiagnosticLogger.Log($"[{DeviceName}] Battery feature found: {_batteryFeature.FeatureName} (ID: {_batteryFeature.FeatureId:X})");

            // Store the feature index for event routing
            _batteryFeatureIndex = FeatureMap[_batteryFeature.FeatureId];

            // Enable battery event reporting (HID++ 1.0 command)
            // Note: Not all devices support this - failures are non-fatal
            try
            {
                var enableCmd = Hidpp10Commands.EnableBatteryReports(DeviceIdx);
                var ret = await Parent.WriteRead10(Parent.DevShort, enableCmd, timeout: 1000);
                if (ret.Length == 0)
                {
                    DiagnosticLogger.Log($"[{DeviceName}] EnableBatteryReports not supported (modern device - events enabled by default)");
                }
                else
                {
                    DiagnosticLogger.Log($"[{DeviceName}] Battery events enabled via HID++ 1.0 command");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogWarning($"[{DeviceName}] Exception enabling battery events: {ex.Message}");
                // Non-fatal - device will fall back to polling
            }
        }
        else
        {
            DiagnosticLogger.LogWarning($"[{DeviceName}] No battery feature found.");
        }

        // Generate stable signature for Native HID device
        // Format: "NATIVE.{deviceType}.{identifier}"
        string deviceSignature = $"NATIVE.{(DeviceType)DeviceType}.{Identifier}";

        HidppManagerContext.Instance.SignalDeviceEvent(
            IPCMessageType.INIT,
            new InitMessage(Identifier, DeviceName, _batteryFeature != null, (DeviceType)DeviceType, deviceSignature)
        );

        DiagnosticLogger.Log($"HID device registered - {Identifier} ({DeviceName}) [Signature: {deviceSignature}]");

        // Mark device as online after successful initialization
        _isOnline = true;

        // Force next battery update to bypass deduplication
        // This ensures fresh timestamp even if battery % unchanged after reconnect
        _forceNextUpdate = true;

        
        if (_batteryFeature == null)
        {
            await Task.Delay(1000, _cancellationSource.Token);
            return;
        }        

        await UpdateBattery();

        // Start battery polling loop with cancellation support
        // Pass pooling token as parameter to avoid disposal race
        _pollingTask = Task.Run(() => PollBattery(_poolingCts.Token), _cancellationSource.Token);

    }

    /// <summary>
    /// Notify device that it has been turned ON.
    /// Starts the battery event delay window if configured.
    /// </summary>
    public void NotifyDeviceOn()
    {
        _deviceOnTime = DateTimeOffset.Now;
        if (_batteryEventDelaySeconds > 0)
        {
            DiagnosticLogger.Log($"[{DeviceName}] Device ON - battery events will be ignored for {_batteryEventDelaySeconds} seconds");
        }
    }

    /// <summary>
    /// Poll the battery status at regular intervals defined in settings
    /// </summary>
    /// <param name="poolingToken">Cancellation token for stopping polling operations</param>
    private async Task PollBattery(CancellationToken poolingToken)
    {
        // Token is captured at Task.Run() call time, preventing disposal race
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationSource.Token, poolingToken);
        var linkedToken = linkedCts.Token;

        var pollInterval = GetPollInterval();
        DiagnosticLogger.Log($"[{DeviceName}] Polling started (interval: {pollInterval}s).");

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.Now;
                var nextPollTime = lastUpdate.AddSeconds(pollInterval);
                var delayMs = Math.Max(0, (int)(nextPollTime - now).TotalMilliseconds);

                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, linkedToken);
                }

                bool updateSucceeded = false;
                try
                {
                    await UpdateBattery();
                    updateSucceeded = true;
                    _consecutivePollFailures = 0; // Reset on success
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogWarning($"[{DeviceName}] Battery update failed: {ex.Message}");
                    // Continue polling - will retry on next iteration
                }

                // CRITICAL: Prevent tight loop on repeated failures
                // Use progressive backoff based on consecutive failures
                if (!updateSucceeded)
                {
                    _consecutivePollFailures++;
                    var retryDelay = GlobalSettings.BatteryBackoff.GetDelay(_consecutivePollFailures);
                    DiagnosticLogger.Log($"[{DeviceName}] Adding {retryDelay.TotalSeconds}s retry delay after failure " +
                                         $"(consecutive failures: {_consecutivePollFailures})");
                    await Task.Delay(retryDelay, linkedToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            DiagnosticLogger.Log($"[{DeviceName}] Polling cancelled");
        }

        DiagnosticLogger.Log($"[{DeviceName}] Polling stopped.");
    }

    private int GetPollInterval()
    {
#if DEBUG
        return 10;
#else
        return Math.Clamp(GlobalSettings.settings.PollPeriod, 20, 3600);
#endif
    }

    public async Task UpdateBattery(bool forceIpcUpdate = false)
    {
        if (_batteryFeature == null)
        {
            DiagnosticLogger.Log($"[{DeviceName}] No battery feature available, skipping battery update.");
            return;
        }

        var ret = await _batteryFeature.GetBatteryAsync(this);

        if (ret == null)
        {
            DiagnosticLogger.Log($"[{DeviceName}] Battery update returned null, skipping.");
            return;
        }

        var batStatus = ret.Value;
        var now = DateTimeOffset.Now;
        lastUpdate = now;

        // Check reconnect flag and consume it
        bool shouldForce = forceIpcUpdate || _forceNextUpdate;
        if (_forceNextUpdate)
        {
            DiagnosticLogger.Log($"[{DeviceName}] Forcing battery update after reconnect");
            _forceNextUpdate = false; // Consume flag
        }

        // Publish update (handles deduplication, IPC, logging)
        _batteryPublisher.PublishUpdate(Identifier, DeviceName, batStatus, now, "poll", shouldForce);
    }

    /// <summary>
    /// Attempt to handle a message as a battery event.
    /// Returns true if the message was a battery event and was handled.
    /// </summary>
    /// <param name="message">The HID++ message to check</param>
    /// <returns>True if this was a battery event and was handled, false otherwise</returns>
    public async Task<bool> TryHandleBatteryEventAsync(Hidpp20 message)
    {
        // Check if we have a battery feature configured
        if (_batteryFeature == null || _batteryFeatureIndex == 0xFF)
        {
            return false;
        }

        // Check if this message is a battery event for our feature
        if (!message.IsBatteryEvent(_batteryFeatureIndex))
        {
            return false;
        }

        // Throttle events to prevent spam (some devices send rapid bursts)
        var now = DateTimeOffset.Now;
        if (!_eventThrottler.ShouldProcessEvent(now))
        {
            DiagnosticLogger.Log($"[{DeviceName}] Battery event throttled (too frequent)");
            return true; // Handled but suppressed
        }

        // Parse the event using the battery feature
        var batteryUpdate = _batteryFeature.ParseBatteryEvent(message);
        if (batteryUpdate == null)
        {
            DiagnosticLogger.LogWarning($"[{DeviceName}] Failed to parse battery event");
            return false;
        }

        var batStatus = batteryUpdate.Value;

        // Exceptional battery event, skip publish (some devices send spurious events)
        // :: band-aid fix disabled for now as we improved IsBatteryEvent

        //if (batStatus.batteryPercentage == 15)
        //{
        //    DiagnosticLogger.Log($"[{DeviceName}] Exceptional battery event detected (Charging at 15%), skipping update publish");
        //    DiagnosticLogger.Log($"[{DeviceName}] Exceptional battery event message: {message}");
        //    return true;

        //}

        // Check if we're in the delay window after device ON (ignore EVENT data during this period)
        if (_batteryEventDelaySeconds > 0 && _deviceOnTime != DateTimeOffset.MinValue)
        {
            var timeSinceDeviceOn = (now - _deviceOnTime).TotalSeconds;
            if (timeSinceDeviceOn < _batteryEventDelaySeconds)
            {
                DiagnosticLogger.Log($"[{DeviceName}] Battery event ignored (device ON +{timeSinceDeviceOn:F1}s, " +
                                   $"delay window {_batteryEventDelaySeconds}s): {batStatus.batteryPercentage}%");

                // Event data suppressed - don't update lastUpdate or cancel polling
                // Let polling continue during delay window for device stability
                return true; // Event handled but data suppressed
            }
        }

        // Normal event processing continues
        lastUpdate = now;

        DiagnosticLogger.Log($"[{DeviceName}] Battery event received: MVolt: {batStatus.batteryMVolt}, Percent: {batStatus.batteryPercentage}");

        // Check if we should stop polling when events arrive
        if (!_keepPollingWithEvents && !_poolingCts.IsCancellationRequested)
        {            
            DiagnosticLogger.Log($"[{DeviceName}] Battery event received, stopping polling (keepPollingWithEvents=false)");            
            _poolingCts.Cancel();
        }
        else if (_keepPollingWithEvents)
        {
            DiagnosticLogger.Log($"[{DeviceName}] Battery event received, polling continues (keepPollingWithEvents=true)");
        }

        
        // Publish update (handles deduplication, IPC, logging)
        _batteryPublisher.PublishUpdate(Identifier, DeviceName, batStatus, now, "event");

        return true; // Event handled successfully
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
            DiagnosticLogger.Log($"[{DeviceName}] HidppDevice.Dispose starting");

            if (disposing)
            {
                // Cancel battery polling task
                _cancellationSource.Cancel();

                // Wait for polling task to exit (with timeout)
                if (_pollingTask != null)
                {
                    try
                    {
                        // Wait up to 10 seconds for task to exit gracefully (increased for safer disposal)
                        bool completed = _pollingTask.Wait(TimeSpan.FromSeconds(10));
                        if (!completed)
                        {
                            DiagnosticLogger.LogWarning($"[{DeviceName}] Battery polling task did not exit within 10s timeout");
                        }
                        else
                        {
                            DiagnosticLogger.Log($"[{DeviceName}] Battery polling task exited successfully");
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.LogWarning($"[{DeviceName}] Error waiting for polling task: {ex.Message}");
                    }
                }

                // Dispose managed resources
                _cancellationSource.Dispose();
                _poolingCts.Dispose();
                _initSemaphore.Dispose();
            }

            DiagnosticLogger.Log($"[{DeviceName}] HidppDevice.Dispose completed");
        }
    }

    ~HidppDevice()
    {
        Dispose(disposing: false);
    }
}
