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
    private readonly CenturionTransport _transport;
    private readonly ushort _usagePage;
    string Tag => $"[Centurion 0x{_usagePage:X4}]";

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
    private string _deviceName = "Centurion Headset";
    private string _identifier = string.Empty;
    private string? _serialNumber;
    private string? _modelId;
    private string? _unitId; // Hardware revision in Centurion terms

    // True while dongle is detected but headset has not yet been contacted.
    // No InitMessage is sent until this is cleared by CompleteInitAsync.
    private volatile bool _pendingInit = false;

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
    private const int HEADSET_ONLINE_DELAY_MS = 1000;

    public CenturionDevice(HidDevicePtr dev, ushort usagePage)
    {
        _transport = CenturionTransportFactory.Create(dev);
        _usagePage = usagePage;
    }

    public async Task InitAsync()
    {
        try
        {
            DiagnosticLogger.Log($"{Tag} Starting feature discovery...");

            // Initialize channels as Direct
            _subChannel = _parentChannel = new CenturionDirectChannel(_transport, _cts.Token);             

            // Start infrastructure tasks first. The dispatcher handles all incoming frames
            // from the read loop and routes them to pending requests or event handlers.
            _readLoopTask = _transport.RunReadLoopAsync(_frameChannel.Writer, _cts.Token);
            _dispatcherTask = ResponseDispatcherAsync(_cts.Token);

            if (_transport.IsPassive)
            {
                DiagnosticLogger.Log($"{Tag} Passive mode — logging frames only, no feature discovery");
                return;
            }

            // Step 1: Discover parent features via CenturionRoot (index 0, func 0)
            _bridgeIdx    = await QueryFeatureIndex(_parentChannel, FEAT_CENTPP_BRIDGE);
            _deviceInfoIdx = await QueryFeatureIndex(_parentChannel, FEAT_DEVICE_INFO);
            byte directBatterySocIdx = await QueryFeatureIndex(_parentChannel, FEAT_BATTERY_SOC);
            _deviceNameIdx = await QueryFeatureIndex(_parentChannel, FEAT_DEVICE_NAME);
            DiagnosticLogger.Log($"{Tag} Feature map: Bridge=0x{_bridgeIdx:X2} DeviceInfo=0x{_deviceInfoIdx:X2} BatterySOC=0x{directBatterySocIdx:X2} DeviceName=0x{_deviceNameIdx:X2}");

            if (_bridgeIdx != 0xFF)
            {
                // DONGLE MODE: headset may be asleep — defer registration until reachable.
                // We start the background loops immediately so bridge events are not missed.
                _subChannel = new CenturionBridgeChannel(_transport, _bridgeIdx, _subDeviceId, _cts.Token);
                _pendingInit = true;
                DiagnosticLogger.Log($"{Tag} Dongle mode — CentPPBridge at index {_bridgeIdx}");

                // Start polling loop
                _pollingTask = Task.Run(() => PollBattery(_cts.Token), _cts.Token);

                // Attempt immediate registration — succeeds if headset already awake.
                await CompleteInitAsync();
            }
            else if (directBatterySocIdx != 0xFF)
            {
                // DIRECT MODE: device is always reachable, register immediately.
                _batteryFeature = new CenturionBatterySOC(directBatterySocIdx, FEAT_BATTERY_SOC);
                DiagnosticLogger.Log($"{Tag} Direct mode — BatterySOC at index {directBatterySocIdx}");

                var reader = new CenturionMetadataReader(_subChannel.SendAsync, _deviceNameIdx, _deviceInfoIdx);
                (_deviceName, _serialNumber, _modelId, _unitId) = await reader.ReadAsync(_deviceName);

                // Generate identifier from real device name and metadata
                _identifier = DeviceIdentifierGenerator.GenerateIdentifier(_serialNumber, _unitId, _modelId, _deviceName);
                string deviceSignature = $"NATIVE.{DeviceType.Headset}.{_identifier}";

                // Signal INIT to UI
                HidppManagerContext.Instance.SignalDeviceEvent(
                    IPCMessageType.INIT,
                    new InitMessage(_identifier, _deviceName, hasBattery: true, DeviceType.Headset, deviceSignature)
                );
                DiagnosticLogger.Log($"{Tag} Device registered: {_deviceName} ({_identifier})");

                // First battery read
                await UpdateBattery(forceUpdate: true);

                // Start polling loop
                _pollingTask = Task.Run(() => PollBattery(_cts.Token), _cts.Token);
            }
            else
            {
                DiagnosticLogger.LogWarning($"{Tag} No CentPPBridge or BatterySOC found — cannot monitor battery");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"{Tag} Init failed: {ex.Message}");
        }
    }

    // ---- Dongle mode: complete registration when headset becomes reachable ----

    /// <summary>
    /// Completes device registration for dongle mode once the headset is reachable.
    /// Safe to call repeatedly — no-ops immediately if already registered (_pendingInit == false).
    /// </summary>
    public async Task CompleteInitAsync()
    {
        if (!_pendingInit) return;

        // Allow only one concurrent init attempt.
        if (!_initLock.Wait(0)) return;

        bool initCompleted = false;
        try
        {
            if (!_pendingInit) return; // Double-check after winning the race

            // Step 1: Contact headset via CentPPBridge.getConnectionInfo (func 0).
            // This is a direct query to the dongle, not a bridge-wrapped request.
            var connResp = await _parentChannel.SendAsync(_bridgeIdx, 0x00, []);
            if (connResp == null)
            {
                DiagnosticLogger.LogWarning($"{Tag} CompleteInit: getConnectionInfo timed out — headset likely asleep");
            }
            else
            {
                DiagnosticLogger.Verbose($"{Tag} CompleteInit getConnectionInfo params: {BitConverter.ToString(connResp.Value.Params)}");
            }
            if (connResp != null && !connResp.Value.HeadsetOffline)
            {
                DiagnosticLogger.Log($"{Tag} Headset connected via bridge");

                // Step 2: Discover sub-device features via bridge
                byte socIdx    = await QueryFeatureIndex(_subChannel, FEAT_BATTERY_SOC);
                byte subNameIdx = await QueryFeatureIndex(_subChannel, FEAT_DEVICE_NAME);
                if (subNameIdx != 0xFF) _deviceNameIdx = subNameIdx;

                byte subInfoIdx = await QueryFeatureIndex(_subChannel, FEAT_DEVICE_INFO);
                if (subInfoIdx != 0xFF) _deviceInfoIdx = subInfoIdx;

                DiagnosticLogger.Log($"{Tag} Bridge sub-features: BatterySOC=0x{socIdx:X2} DeviceName=0x{subNameIdx:X2} DeviceInfo=0x{subInfoIdx:X2}");

                if (socIdx != 0xFF)
                {
                    _batteryFeature = new CenturionBatterySOC(socIdx, FEAT_BATTERY_SOC);

                    // Step 3: Fetch metadata (name, HW info, serial)
                    var reader = new CenturionMetadataReader(_subChannel.SendAsync, _deviceNameIdx, _deviceInfoIdx);
                    (_deviceName, _serialNumber, _modelId, _unitId) = await reader.ReadAsync(_deviceName);
                    initCompleted = true;
                }
                else
                {
                    DiagnosticLogger.LogWarning($"{Tag} CompleteInit: BatterySOC feature not found via bridge — cannot monitor battery");
                }
            }
            else if (connResp != null)
            {
                DiagnosticLogger.Log($"{Tag} CompleteInit: headset offline (HeadsetOffline=true), will retry on connect event");
            }
        }
        finally
        {
            if (initCompleted)
            {
                // Step 4: Compute identifier and register with UI.
                // _pendingInit is cleared here, inside the lock, so a concurrent CompleteInitAsync
                // caller cannot slip through and emit a duplicate InitMessage.
                _identifier = DeviceIdentifierGenerator.GenerateIdentifier(_serialNumber, _unitId, _modelId, _deviceName);
                string deviceSignature = $"NATIVE.{DeviceType.Headset}.{_identifier}";

                HidppManagerContext.Instance.SignalDeviceEvent(
                    IPCMessageType.INIT,
                    new InitMessage(_identifier, _deviceName, true, DeviceType.Headset, deviceSignature)
                );

                DiagnosticLogger.Log($"{Tag} Device registered: {_deviceName} ({_identifier})");
                _pendingInit = false; // volatile write — establishes happens-before for _identifier visibility
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
        DiagnosticLogger.Log($"{Tag} Dispatcher started");
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
                    var toForward = _subChannel.RouteEvent(frame);
                    if (toForward != null)
                        await HandleAsyncEvent(toForward.Value);
                    // else: consumed by channel (bridge ACK or solicited sub-response)
                    continue;
                }

                DiagnosticLogger.Verbose($"{Tag} Dispatcher: ignored frame SwId=0x{frame.SwId:X2} feat=0x{frame.FeatIdx:X2}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"{Tag} Dispatcher exception: {ex.Message}");
        }
        DiagnosticLogger.Log($"{Tag} Dispatcher ended");
    }

    /// <summary>
    /// Handles unsolicited events (SwId==0x00): battery updates or bridge connection changes.
    /// Bridge MessageEvent (FuncId==0x01) unwrapping is handled upstream by CenturionBridgeChannel.
    /// </summary>
    private async Task HandleAsyncEvent(CenturionResponse frame)
    {
        if (_batteryFeature != null && frame.FeatIdx == _batteryFeature.FeatureIndex)
        {
            var batState = _batteryFeature.ParseBatteryParams(frame.Params);
            if (batState != null && !string.IsNullOrEmpty(_identifier))
                _batteryPublisher.PublishUpdate(_identifier, _deviceName, batState.Value, DateTimeOffset.Now, "event");

            return;
        }

        if (frame.FeatIdx == _bridgeIdx && frame.FuncId == 0x00)
            await HandleBridgeConnectionEvent(frame);
    }

    /// <summary>
    /// Handles CentPPBridge ConnectionStateChanged event (FeatIdx==_bridgeIdx, FuncId==0x00).
    /// </summary>
    private async Task HandleBridgeConnectionEvent(CenturionResponse resp)
    {
        if (resp.HeadsetOnline)
        {
            DiagnosticLogger.Log($"{Tag} Bridge: headset connected");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(HEADSET_ONLINE_DELAY_MS, _cts.Token); // Wait for RF stability
                    if (_pendingInit) await CompleteInitAsync();
                    else await UpdateBattery(forceUpdate: true);
                }
                catch (OperationCanceledException) { } // Prevent access to disposed objects when shutting down
            });
        }
        else
        {
            DiagnosticLogger.Log($"{Tag} Bridge: headset disconnected");
            if (!_pendingInit && !string.IsNullOrEmpty(_identifier))
            {
                HidppManagerContext.Instance.SignalDeviceEvent(
                    IPCMessageType.UPDATE,
                    new UpdateMessage(_identifier, -1, PowerSupplyStatus.UNKNOWN, 0, DateTimeOffset.Now)
                );
            }
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
            DiagnosticLogger.Verbose($"{Tag} Battery update: querying feat=0x{_batteryFeature.FeatureIndex:X2}");
            var resp = await _subChannel.SendAsync(_batteryFeature.FeatureIndex, 0x00, []);
            if (resp == null)
            {
                DiagnosticLogger.LogWarning($"{Tag} Battery update: no response (timeout or device offline)");
                return false;
            }

            var batState = _batteryFeature.ParseBatteryParams(resp.Value.Params);
            if (batState == null)
            {
                DiagnosticLogger.LogWarning($"{Tag} Battery update: parse failed (params={BitConverter.ToString(resp.Value.Params)})");
                return false;
            }

            _batteryPublisher.PublishUpdate(_identifier, _deviceName, batState.Value, DateTimeOffset.Now, "poll", forceUpdate);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"{Tag} Battery update exception: {ex.Message}");
            return false;
        }
    }

    private async Task PollBattery(CancellationToken ct)
    {
        DiagnosticLogger.Log($"{Tag} Battery polling started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_pendingInit)
                    await CompleteInitAsync();
                else
                    await UpdateBattery();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                DiagnosticLogger.LogWarning($"{Tag} Poll error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(GlobalSettings.settings.PollPeriod, 20, 3600)), ct);
        }
        DiagnosticLogger.Log($"{Tag} Battery polling stopped");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        DiagnosticLogger.Log($"{Tag} Disposing Centurion device: {_deviceName}");

        _cts.Cancel();

        // Wait briefly for tasks to exit
        try { _pollingTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _readLoopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _dispatcherTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        // Only send offline notification if device was fully registered with the UI
        if (!_pendingInit && !string.IsNullOrEmpty(_identifier))
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
        _transport.Dispose();
        _initLock.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
