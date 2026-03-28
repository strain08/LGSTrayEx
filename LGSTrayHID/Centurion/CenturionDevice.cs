using LGSTrayHID.Battery;
using LGSTrayHID.Centurion.Channel;
using LGSTrayHID.Centurion.Features;
using LGSTrayHID.Centurion.Transport;
using LGSTrayHID.HidApi;
using LGSTrayHID.Metadata;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using System.Threading.Channels;

namespace LGSTrayHID.Centurion;

/// <summary>
/// Manages feature discovery and battery polling for a single Centurion headset.
/// Supports both dongle mode (via CentPPBridge) and direct USB mode.
///
/// Centurion feature IDs:
///   0x0000 = CenturionRoot (getFeature discovery)
///   0x0001 = FeatureSet
///   0x0003 = CentPPBridge (dongle-to-headset bridge)
///   0x0100 = DeviceInfo
///   0x0101 = DeviceName
///   0x0104 = BatterySOC
/// </summary>
public class CenturionDevice : IDisposable
{
    private readonly HidDevicePtr _dev;
    private CenturionTransport _transport = null!;
    private readonly ushort _usagePage;
    private readonly ushort _productId;

    private readonly string _tag;

    /// <summary>Operational mode — set once during InitAsync, then updated by bridge lifecycle events.</summary>
    private enum CenturionMode
    {
        Discovering,       // InitAsync in progress — feature queries not yet complete
        DirectUSB,         // Wired USB: battery queried directly on the parent channel
        DonglePending,     // Dongle found, headset not yet reachable (asleep or connecting)
        DongleReady,       // Dongle + headset reachable, normal battery polling via bridge
        DeferredDiscovery, // Both init paths failed — periodic retry until bridge appears
    }

    private volatile CenturionMode _mode = CenturionMode.Discovering;

    // Feature indices discovered at init
    private byte _bridgeIdx = 0xFF;     // CentPPBridge (0x0003) — 0xFF = not found
    private byte _deviceNameIdx = 0xFF; // DeviceName (0x0101) — 0xFF = not found
    private byte _deviceInfoIdx = 0xFF; // DeviceInfo (0x0100) — 0xFF = not found

    // Channel abstraction — _parentChannel is always direct; _subChannel may be a bridge channel
    private CenturionDirectChannel _parentChannel = null!; // initialized in InitAsync
    private CenturionChannel _subChannel = null!;          // = _parentChannel or CenturionBridgeChannel

    // Battery feature — null until discovered
    private ICenturionBatteryFeature? _batteryFeature;

    // Device state
    private const byte _subDeviceId = 0x00;   // Solaar's comment: device_id=0 for the headset
    private string _deviceName;
    private string _identifier = string.Empty;
    private DeviceIdentity _deviceIdentity;

    // Channel-based I/O infrastructure
    private readonly Channel<CenturionResponse> _frameChannel = System.Threading.Channels.Channel.CreateUnbounded<CenturionResponse>();

    // Background tasks
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoopTask;
    private Task? _dispatcherTask;
    private Task? _pollingTask;

    // Concurrency
    private readonly SemaphoreSlim _initLock = new(1, 1); // Guards CompleteInitAsync — non-blocking tryacquire
    private readonly BatteryUpdatePublisher _batteryPublisher = new();
    private int _disposed = 0;

    // Centurion feature IDs
    private const ushort FEAT_CENTPP_BRIDGE = 0x0003;
    private const ushort FEAT_DEVICE_INFO   = 0x0100;
    private const ushort FEAT_DEVICE_NAME   = 0x0101;
    private const ushort FEAT_BATTERY_SOC   = 0x0104;

    // Workflow delays
    private const int HEADSET_ONLINE_DELAY_MS = 1000;    // time to wait for RF link to stabilise after wakeup
    private const int PENDING_INIT_POLL_INTERVAL_S = 15;

    public CenturionDevice(HidDevicePtr dev, ushort usagePage, ushort productId, string? productName = null)
    {
        _dev = dev;
        _usagePage = usagePage;
        _productId = productId;
        _deviceName = productName ?? "Centurion Headset";
        _tag = $"[0x{_productId:X4}] Device";
    }

    public async Task InitAsync()
    {
        try
        {
            _transport = await CenturionTransportFactory.CreateAsync(_dev, _productId, _cts.Token);
            DiagnosticLogger.Log($"{_tag} Starting feature discovery...");

            // Initialize channels as Direct
            _subChannel = _parentChannel = new CenturionDirectChannel(_transport, _cts.Token);

            // Start infrastructure tasks first. The dispatcher handles all incoming frames
            // from the read loop and routes them to pending requests or event handlers.
            _readLoopTask = _transport.RunReadLoopAsync(_frameChannel.Writer, _cts.Token);
            _dispatcherTask = ResponseDispatcherAsync(_cts.Token);

            if (_transport.IsPassive)
            {
                DiagnosticLogger.Log($"{_tag} Passive mode — logging frames only, no feature discovery");
                return;
            }

            // Step 1: Discover parent features via CenturionRoot (index 0, func 0)
            _bridgeIdx    = await QueryFeatureIndex(_parentChannel, FEAT_CENTPP_BRIDGE);
            _deviceInfoIdx = await QueryFeatureIndex(_parentChannel, FEAT_DEVICE_INFO);
            byte directBatterySocIdx = await QueryFeatureIndex(_parentChannel, FEAT_BATTERY_SOC);
            _deviceNameIdx = await QueryFeatureIndex(_parentChannel, FEAT_DEVICE_NAME);
            DiagnosticLogger.Log($"{_tag} Feature map: Bridge=0x{_bridgeIdx:X2} DeviceInfo=0x{_deviceInfoIdx:X2} BatterySOC=0x{directBatterySocIdx:X2} DeviceName=0x{_deviceNameIdx:X2}");

            if (_bridgeIdx != 0xFF)
            {
                // DONGLE MODE: headset may be asleep — defer registration until reachable.
                // We start the background loops immediately so bridge events are not missed.
                _subChannel = new CenturionBridgeChannel(_transport, _bridgeIdx, _subDeviceId, _cts.Token);
                _mode = CenturionMode.DonglePending;
                DiagnosticLogger.Log($"{_tag} Dongle mode — CentPPBridge at index {_bridgeIdx}");

                // Start polling loop
                _pollingTask = Task.Run(() => PollBattery(_cts.Token), _cts.Token);

                // Attempt immediate registration — succeeds if headset already awake.
                await CompleteInitAsync();
            }
            else if (directBatterySocIdx != 0xFF)
            {
                // DIRECT MODE: device is always reachable, register immediately.
                _batteryFeature = new CenturionBatterySOC(directBatterySocIdx, FEAT_BATTERY_SOC);
                _mode = CenturionMode.DirectUSB;
                DiagnosticLogger.Log($"{_tag} Direct mode — BatterySOC at index {directBatterySocIdx}");

                var reader = new CenturionMetadataReader(_subChannel.SendAsync, _deviceNameIdx, _deviceInfoIdx);
                (_deviceName, _deviceIdentity) = await reader.ReadAsync(defaultDeviceName: _deviceName);

                // Generate identifier from real device name and metadata
                _identifier = DeviceIdentifierGenerator.GenerateIdentifier(_deviceIdentity, _deviceName);

                // Signal INIT to UI
                HidppManagerContext.Instance.SignalDeviceEvent(
                    IPCMessageType.INIT,
                    new InitMessage(_identifier, _deviceName, hasBattery: true, DeviceType.Headset)
                );
                DiagnosticLogger.Log($"{_tag} Device registered: {_deviceName} ({_identifier})");

                // First battery read
                await UpdateBattery(forceUpdate: true);

                // Start polling loop
                _pollingTask = Task.Run(() => PollBattery(_cts.Token), _cts.Token);
            }
            else
            {
                // Feature discovery failed — headset is likely asleep. Enter deferred-discovery mode:
                // the polling loop will retry discovery and bridge events will infer the bridge index.
                _mode = CenturionMode.DeferredDiscovery;
                DiagnosticLogger.LogWarning($"{_tag} Feature discovery timed out — entering deferred-discovery mode (headset likely asleep)");
                _pollingTask = Task.Run(() => PollBattery(_cts.Token), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            DiagnosticLogger.Log($"{_tag} Init cancelled (device removed during startup)");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"{_tag} Init failed: {ex.Message}");
        }
    }

    // ---- Dongle mode: complete registration when headset becomes reachable ----

    /// <summary>
    /// Completes device registration for dongle mode once the headset is reachable.
    /// Safe to call repeatedly — no-ops immediately if mode is not DonglePending.
    /// </summary>
    /// <param name="headsetOnlineConfirmed">
    /// When true, skip getConnectionInfo — caller already knows the headset is online
    /// (e.g. from a ConnectionStateChanged event). Avoids a redundant query that may
    /// time out during the dongle's own post-wakeup enumeration.
    /// </param>
    public async Task CompleteInitAsync(bool headsetOnlineConfirmed = false)
    {
        if (_mode != CenturionMode.DonglePending) return;

        // Allow only one concurrent init attempt.
        if (!_initLock.Wait(0)) return;

        bool initCompleted = false;
        try
        {
            if (_mode != CenturionMode.DonglePending) return; // Double-check after winning the race

            if (!headsetOnlineConfirmed)
            {
                // Step 1: Contact headset via CentPPBridge.getConnectionInfo (func 0).
                // This is a direct query to the dongle, not a bridge-wrapped request.
                var connResp = await _parentChannel.SendAsync(_bridgeIdx, 0x00, []);
                if (connResp == null)
                {
                    DiagnosticLogger.LogWarning($"{_tag} CompleteInit: getConnectionInfo timed out — headset likely asleep");
                    return;
                }

                DiagnosticLogger.Verbose($"{_tag} CompleteInit getConnectionInfo params: {BitConverter.ToString(connResp.Value.Params)}");
                if (connResp.Value.ConnectionState != HeadsetConnectionState.Online)
                {
                    DiagnosticLogger.Log($"{_tag} CompleteInit: headset not online ({connResp.Value.ConnectionState}), will retry on connect event");
                    return;
                }
            }

            DiagnosticLogger.Log($"{_tag} Headset connected via bridge" +
                (headsetOnlineConfirmed ? " (confirmed by event)" : ""));

            // Step 2: Discover sub-device features via bridge
            byte socIdx     = await QueryFeatureIndex(_subChannel, FEAT_BATTERY_SOC);
            byte subNameIdx = await QueryFeatureIndex(_subChannel, FEAT_DEVICE_NAME);
            if (subNameIdx != 0xFF) _deviceNameIdx = subNameIdx;

            byte subInfoIdx = await QueryFeatureIndex(_subChannel, FEAT_DEVICE_INFO);
            if (subInfoIdx != 0xFF) _deviceInfoIdx = subInfoIdx;

            DiagnosticLogger.Log($"{_tag} Bridge sub-features: BatterySOC=0x{socIdx:X2} DeviceName=0x{subNameIdx:X2} DeviceInfo=0x{subInfoIdx:X2}");

            if (socIdx != 0xFF)
            {
                _batteryFeature = new CenturionBatterySOC(socIdx, FEAT_BATTERY_SOC);

                // Step 3: Fetch metadata (name, HW info, serial)
                var reader = new CenturionMetadataReader(_subChannel.SendAsync, _deviceNameIdx, _deviceInfoIdx);
                (_deviceName, _deviceIdentity) = await reader.ReadAsync(_deviceName);
                initCompleted = true;
            }
            else
            {
                DiagnosticLogger.LogWarning($"{_tag} CompleteInit: BatterySOC feature not found via bridge — cannot monitor battery");
            }
        }
        finally
        {
            if (initCompleted)
            {
                // Step 4: Compute identifier and register with UI.
                // _mode is set to DongleReady here, inside the lock, so a concurrent CompleteInitAsync
                // caller cannot slip through and emit a duplicate InitMessage.
                _identifier = DeviceIdentifierGenerator.GenerateIdentifier(_deviceIdentity, _deviceName);

                HidppManagerContext.Instance.SignalDeviceEvent(
                    IPCMessageType.INIT,
                    new InitMessage(_identifier, _deviceName, true, DeviceType.Headset)
                );

                DiagnosticLogger.Log($"{_tag} Device registered: {_deviceName} ({_identifier})");
                _mode = CenturionMode.DongleReady; // volatile write — establishes happens-before for _identifier visibility
            }
            _initLock.Release();
        }

        if (initCompleted)
        {
            // Step 5: First battery read (outside the lock — no need to block concurrent init callers).
            await UpdateBattery(forceUpdate: true);
        }
    }

    /// <summary>
    /// Processes frames as they arrive, routing them to the channel or event handlers.
    /// </summary>
    private async Task ResponseDispatcherAsync(CancellationToken ct)
    {
        DiagnosticLogger.Log($"{_tag} Dispatcher started");
        try
        {
            await foreach (var frame in _frameChannel.Reader.ReadAllAsync(ct))
            {
                if (frame.SwId == CenturionTransport.SWID)
                {
                    // Fall back to parent channel for direct queries in bridge mode
                    // (e.g. CentPPBridge.getConnectionInfo sent via _parentChannel).
                    bool handled = _subChannel.TryCompleteRequest(frame);
                    if (!handled && !ReferenceEquals(_subChannel, _parentChannel))
                        _parentChannel.TryCompleteRequest(frame);

                    continue;
                }

                if (frame.SwId == 0x00)
                {
                    // Centurion error response: feat=0xFF, swid=0, params=[original_swid, error_code].
                    // The device sends this when a feature doesn't exist on the parent.
                    // Fail the pending request immediately instead of letting it timeout.
                    if (frame.FeatIdx == 0xFF && frame.Params.Length >= 1 && frame.Params[0] == CenturionTransport.SWID)
                    {
                        DiagnosticLogger.Verbose($"{_tag} Dispatcher: error response (feat=0xFF) — feature not found");
                        bool handled = _subChannel.TryCompleteError(frame);
                        if (!handled && !ReferenceEquals(_subChannel, _parentChannel))
                            _parentChannel.TryCompleteError(frame);
                        continue;
                    }

                    var toForward = _subChannel.RouteEvent(frame);
                    if (toForward != null)
                        await HandleAsyncEvent(toForward.Value);
                    // else: consumed by channel (bridge ACK or solicited sub-response)
                    continue;
                }

                DiagnosticLogger.Verbose($"{_tag} Dispatcher: ignored frame SwId=0x{frame.SwId:X2} feat=0x{frame.FeatIdx:X2}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"{_tag} Dispatcher exception: {ex.Message}");
        }
        DiagnosticLogger.Log($"{_tag} Dispatcher ended");
    }

    /// <summary>
    /// Handles unsolicited events (SwId==0x00): battery updates or bridge connection changes.
    /// Bridge MessageEvent (FuncId==0x01) unwrapping is handled upstream by CenturionBridgeChannel.
    /// </summary>
    private async Task HandleAsyncEvent(CenturionResponse frame)
    {
        // Bridge index not yet known — infer it from spontaneous ConnectionStateChanged events.
        // The dongle emits feat=N, func=0, swid=0 frames when the headset wakes.
        if (_mode == CenturionMode.DeferredDiscovery &&
            frame.FeatIdx != 0xFF && frame.FuncId == 0x00 && 
            frame.ConnectionState == HeadsetConnectionState.Online)
        {
            _bridgeIdx = frame.FeatIdx;
            _subChannel = new CenturionBridgeChannel(_transport, _bridgeIdx, _subDeviceId, _cts.Token);
            _mode = CenturionMode.DonglePending;
            DiagnosticLogger.Log($"{_tag} Bridge index inferred from ConnectionStateChanged event: feat=0x{_bridgeIdx:X2}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(HEADSET_ONLINE_DELAY_MS, _cts.Token);
                    await CompleteInitAsync(headsetOnlineConfirmed: true);
                }
                catch (OperationCanceledException) { }
            });
            return;
        }

        if (_batteryFeature != null && frame.FeatIdx == _batteryFeature.FeatureIndex)
        {
            var batState = _batteryFeature.ParseBatteryParams(frame.Params);
            if (batState != null && !string.IsNullOrEmpty(_identifier))
                _batteryPublisher.PublishUpdate(_identifier, _deviceName, batState.Value, DateTimeOffset.Now, "event");

            return;
        }

        if (_mode is CenturionMode.DonglePending or CenturionMode.DongleReady
            && frame.FeatIdx == _bridgeIdx && frame.FuncId == 0x00)
            await HandleBridgeConnectionEvent(frame);
    }

    /// <summary>
    /// Handles CentPPBridge ConnectionStateChanged event (FeatIdx==_bridgeIdx, FuncId==0x00).
    /// </summary>
    private async Task HandleBridgeConnectionEvent(CenturionResponse resp)
    {
        switch (resp.ConnectionState)
        {
            case HeadsetConnectionState.Online:
                DiagnosticLogger.Log($"{_tag} Bridge: headset connected");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(HEADSET_ONLINE_DELAY_MS, _cts.Token); // Wait for RF stability
                        if (_mode == CenturionMode.DonglePending) await CompleteInitAsync(headsetOnlineConfirmed: true);
                        else await UpdateBattery(forceUpdate: true);
                    }
                    catch (OperationCanceledException) { } // Prevent access to disposed objects when shutting down
                });
                break;

            case HeadsetConnectionState.Offline:
                DiagnosticLogger.Log($"{_tag} Bridge: headset disconnected");
                if (_mode == CenturionMode.DongleReady && !string.IsNullOrEmpty(_identifier))
                {
                    HidppManagerContext.Instance.SignalDeviceEvent(
                        IPCMessageType.UPDATE,
                        new UpdateMessage(_identifier, -1, PowerSupplyStatus.UNKNOWN, 0, DateTimeOffset.Now)
                    );
                }
                break;

            case HeadsetConnectionState.Unknown:
                DiagnosticLogger.LogWarning($"{_tag} Bridge: connection event with empty params — ignoring");
                break;
        }
    }

    /// <summary>
    /// Query CenturionRoot (index 0, func 0 = getFeature) for a feature's index on the given channel.
    /// Returns 0xFF if not found.
    /// </summary>
    private async Task<byte> QueryFeatureIndex(CenturionChannel channel, ushort featureId)
    {
        byte[] featureIdBytes = [(byte)(featureId >> 8), (byte)(featureId & 0xFF)];
        var resp = await channel.SendAsync(0x00, 0x00, featureIdBytes);
        if (resp == null || resp.Value.Params.Length == 0)
            return 0xFF;

        byte index = resp.Value.Params[0];
        if (index == 0 && featureId != 0x0000)
            return 0xFF;

        return index;
    }

    private async Task<bool> UpdateBattery(bool forceUpdate = false)
    {
        if (_batteryFeature == null || string.IsNullOrEmpty(_identifier)) return false;
        try
        {
            DiagnosticLogger.Verbose($"{_tag} Battery update: querying feat=0x{_batteryFeature.FeatureIndex:X2}");
            var resp = await _subChannel.SendAsync(_batteryFeature.FeatureIndex, 0x00, []);
            if (resp == null)
            {
                DiagnosticLogger.LogWarning($"{_tag} Battery update: no response (timeout or device offline)");
                return false;
            }

            var batState = _batteryFeature.ParseBatteryParams(resp.Value.Params);
            if (batState == null)
            {
                DiagnosticLogger.LogWarning($"{_tag} Battery update: parse failed (params={BitConverter.ToString(resp.Value.Params)})");
                return false;
            }

            _batteryPublisher.PublishUpdate(_identifier, _deviceName, batState.Value, DateTimeOffset.Now, "poll", forceUpdate);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"{_tag} Battery update exception: {ex.Message}");
            return false;
        }
    }

    private async Task RetryBridgeDiscoveryAsync()
    {
        DiagnosticLogger.Verbose($"{_tag} Retrying bridge discovery...");

        byte bridgeIdx = await QueryFeatureIndex(_parentChannel, FEAT_CENTPP_BRIDGE);
        if (bridgeIdx == 0xFF) return; // Still not responding

        // Bridge found — configure channel and trigger init
        _bridgeIdx     = bridgeIdx;
        _deviceInfoIdx = await QueryFeatureIndex(_parentChannel, FEAT_DEVICE_INFO);
        _deviceNameIdx = await QueryFeatureIndex(_parentChannel, FEAT_DEVICE_NAME);

        _subChannel = new CenturionBridgeChannel(_transport, _bridgeIdx, _subDeviceId, _cts.Token);
        _mode = CenturionMode.DonglePending;

        DiagnosticLogger.Log($"{_tag} Bridge discovered on retry: index 0x{_bridgeIdx:X2}");
        await CompleteInitAsync();
    }

    private async Task PollBattery(CancellationToken ct)
    {
        DiagnosticLogger.Log($"{_tag} Battery polling started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                switch (_mode)
                {
                    case CenturionMode.DeferredDiscovery: 
                        await RetryBridgeDiscoveryAsync(); 
                        break;
                    case CenturionMode.DonglePending:     
                        await CompleteInitAsync();         
                        break;
                    case CenturionMode.DongleReady or CenturionMode.DirectUSB:                    
                        await UpdateBattery();
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                DiagnosticLogger.LogWarning($"{_tag} Poll error: {ex.Message}");
            }

            TimeSpan delay = _mode is CenturionMode.DeferredDiscovery or CenturionMode.DonglePending
                ? TimeSpan.FromSeconds(PENDING_INIT_POLL_INTERVAL_S)
                : TimeSpan.FromSeconds(Math.Clamp(GlobalSettings.settings.PollPeriod, 20, 3600));
            await Task.Delay(delay, ct);
        }
        DiagnosticLogger.Log($"{_tag} Battery polling stopped");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        DiagnosticLogger.Log($"{_tag} Disposing Centurion device: {_deviceName}");

        _cts.Cancel();

        // Wait briefly for tasks to exit (in parallel — max 2s total)
        var tasks = new[] { _pollingTask, _readLoopTask, _dispatcherTask }
            .Where(t => t != null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
            try { Task.WaitAll(tasks, TimeSpan.FromSeconds(2)); } catch { }

        // Only send offline notification if device was fully registered with the UI
        if (_mode is CenturionMode.DongleReady or CenturionMode.DirectUSB
            && !string.IsNullOrEmpty(_identifier))
        {
            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.UPDATE,
                new UpdateMessage(
                    deviceId: _identifier,
                    batteryPercentage: -1,
                    powerSupplyStatus: PowerSupplyStatus.UNKNOWN,
                    batteryMVolt: 0,
                    updateTime: DateTimeOffset.Now
                )
            );
        }

        _parentChannel?.Dispose();
        if (_subChannel != null && !ReferenceEquals(_subChannel, _parentChannel))
            _subChannel.Dispose();
        if (_transport != null)
            _transport.Dispose();
        else
            HidApi.HidApi.HidClose(_dev); // transport never created — close handle directly
        _initLock.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
