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

    // Battery event tracking
    private byte _batteryFeatureIndex = 0xFF; // 0xFF = not set
    private readonly BatteryEventThrottler _eventThrottler = new(500); // 500ms throttle window
    private readonly BatteryUpdatePublisher _batteryPublisher = new(); // Handles deduplication and IPC

    // Semaphore to prevent concurrent InitAsync calls
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    // Disposal and cancellation support
    private readonly CancellationTokenSource _cancellationSource = new();

    private Task? _pollingTask;
    private readonly CancellationTokenSource _poolingCts = new();

    private int _disposeCount = 0;
    public bool Disposed => _disposeCount > 0;

    public HidppDevice(HidppReceiver parent, byte deviceIdx)
    {
        Parent = parent;
        DeviceIdx = deviceIdx;
    }

    public async Task InitAsync()
    {
        await _initSemaphore.WaitAsync();
        try
        {
            Hidpp20 ret;

            // Sync Ping with retry logic for sleeping devices
            const int maxRetries = 10;
            const int initialDelay = 2000; // 2 seconds
            bool pingSuccess = false;

            for (int retry = 0; retry < maxRetries && !pingSuccess; retry++)
            {
                // Add delay before retry attempts (not on first attempt)
                if (retry > 0)
                {
                    int delay = initialDelay * (int)Math.Pow(2, retry - 1);
                    DiagnosticLogger.Log($"Retrying HID device index {DeviceIdx} after {delay}ms delay (attempt {retry + 1}/{maxRetries})");
                    await Task.Delay(delay);
                }

                // Ping test
                int successCount = 0;
                int successThresh = 3;
                DiagnosticLogger.Log($"Starting ping test for HID device index {DeviceIdx}");
                for (int i = 0; i < 10; i++)
                {
                    var ping = await Parent.Ping20(DeviceIdx, AppConstants.INIT_PING_TIMEOUT_MS);
                    if (ping)
                    {
                        successCount++;
                    }
                    else
                    {
                        successCount = 0;
                    }

                    if (successCount >= successThresh)
                    {
                        pingSuccess = true;
                        break;
                    }
                }

                // Log result if this is the last attempt and still failing
                if (!pingSuccess && retry == maxRetries - 1)
                {
                    DiagnosticLogger.LogWarning($"HID device index {DeviceIdx} failed ping test after {maxRetries} retries ({successCount}/{successThresh} successes)");
                    return;
                }
            }

            DiagnosticLogger.Log($"HID device index {DeviceIdx} passed ping test");

            // Find IFeatureSet (0x0001) - get its feature index
            ret = await Parent.WriteRead20(Parent.DevShort,
                Hidpp20Commands.GetFeatureIndex(DeviceIdx, HidppFeature.FEATURE_SET));
            FeatureMap[HidppFeature.FEATURE_SET] = ret.GetParam(0);

            // Get Feature Count
            ret = await Parent.WriteRead20(Parent.DevShort,
                Hidpp20Commands.GetFeatureCount(DeviceIdx, FeatureMap[HidppFeature.FEATURE_SET]));
            int featureCount = ret.GetParam(0);

            // Enumerate Features
            for (byte i = 0; i <= featureCount; i++)
            {
                ret = await Parent.WriteRead20(Parent.DevShort,
                    Hidpp20Commands.EnumerateFeature(DeviceIdx, FeatureMap[HidppFeature.FEATURE_SET], i), AppConstants.WRITE_READ_TIMEOUT_MS);

                // Check if we got a valid response (timeout returns empty array)
                if (ret.Length == 0)
                {
                    DiagnosticLogger.LogWarning($"[Device {DeviceIdx}] Feature enumeration timeout at index {i}, stopping enumeration");
                    break;
                }

                ushort featureId = ret.GetFeatureId();

                FeatureMap[featureId] = i;

                // Log feature mapping for debugging connection events
                DiagnosticLogger.Log($"[Device {DeviceIdx}] Feature 0x{featureId:X04} mapped to index {i}");
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
                await Parent.WriteRead10(Parent.DevShort, enableCmd, timeout: 1000);
                DiagnosticLogger.Log($"[{DeviceName}] Battery events enabled");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogWarning($"[{DeviceName}] Failed to enable battery events (device may not support): {ex.Message}");
                // Non-fatal - device will fall back to polling
            }
        }
        else
        {
            DiagnosticLogger.LogWarning($"[{DeviceName}] No battery feature found.");
        }

        HidppManagerContext.Instance.SignalDeviceEvent(
            IPCMessageType.INIT,
            new InitMessage(Identifier, DeviceName, _batteryFeature != null, (DeviceType)DeviceType)
        );

        DiagnosticLogger.Log($"HID device registered - {Identifier} ({DeviceName})");

        await Task.Delay(1000);
        if (_batteryFeature == null) return;

        // Start battery polling loop with cancellation support
        _pollingTask = Task.Run(() => PollBattery(_cancellationSource.Token, _poolingCts.Token), _cancellationSource.Token);

    }

    /// <summary>
    /// Poll the battery status at regular intervals defined in settings RetryTime
    /// </summary>
    /// <param name="lifeCycle"></param>
    /// <param name="pooling"></param>
    /// <returns></returns>
    private async Task PollBattery(CancellationToken lifeCycle, CancellationToken pooling)
    {
        while (!lifeCycle.IsCancellationRequested && !pooling.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
#if DEBUG
            var expectedUpdateTime = lastUpdate.AddSeconds(1);
#else
                        var expectedUpdateTime = lastUpdate.AddSeconds(GlobalSettings.settings.PollPeriod);
#endif
            if (now < expectedUpdateTime)
            {
                await Task.Delay((int)(expectedUpdateTime - now).TotalMilliseconds);
            }
            try
            {
                await UpdateBattery();
            }
            catch
            {
                break;
            }

            await Task.Delay(GlobalSettings.settings.RetryTime * 1000);
            DiagnosticLogger.Log($"Polling battery for device {DeviceName}");
        }
        DiagnosticLogger.Log($"Pooling stopped for {DeviceName}.");
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

        // Publish update (handles deduplication, IPC, logging)
        _batteryPublisher.PublishUpdate(Identifier, DeviceName, batStatus, now, "poll", forceIpcUpdate);
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
        lastUpdate = now;

        if (!_poolingCts.IsCancellationRequested)
        {
            DiagnosticLogger.Log($"[{DeviceName}] Battery event received, stopping polling");
            _poolingCts.Cancel(); // Stop polling when we get an event
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
                        // Wait up to 5 seconds for task to exit gracefully
                        bool completed = _pollingTask.Wait(TimeSpan.FromSeconds(5));
                        if (!completed)
                        {
                            DiagnosticLogger.LogWarning($"[{DeviceName}] Battery polling task did not exit within timeout");
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.LogWarning($"[{DeviceName}] Error waiting for polling task: {ex.Message}");
                    }
                }

                // Dispose managed resources
                _cancellationSource.Dispose();
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
