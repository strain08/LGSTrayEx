using LGSTrayHID.Battery;
using LGSTrayHID.Features;
using LGSTrayHID.HidApi;
using LGSTrayHID.Metadata;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    private byte _batterySocIdx = 0xFF; // BatterySOC (0x0104) — 0xFF = not found
    private byte _deviceNameIdx = 0xFF; // DeviceName (0x0101) — 0xFF = not found
    private byte _deviceInfoIdx = 0xFF; // DeviceInfo (0x0100) — 0xFF = not found

    // Device state
    private bool _isDongleMode;
    private const byte _subDeviceId = 0x00;   // Solaar's comment # device_id=0 for the headset 
    private string _deviceName = "Centurion Headset";
    private string _identifier = string.Empty;
    private string? _serialNumber;
    private string? _modelId;
    private string? _unitId; // Hardware revision in Centurion terms

    // True while dongle is detected but headset has not yet been contacted.
    // No InitMessage is sent until this is cleared by CompleteInitAsync.
    private volatile bool _pendingInit = false;

    // Channel-based I/O infrastructure
    private readonly Channel<CenturionResponse> _frameChannel = Channel.CreateUnbounded<CenturionResponse>();
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<CenturionResponse>> _pendingRequests = new();

    // Background tasks
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoopTask;
    private Task? _dispatcherTask;
    private Task? _pollingTask;

    // Concurrency
    private readonly SemaphoreSlim _ioLock = new(1, 1); // Protects Write path
    private readonly BatteryUpdatePublisher _batteryPublisher = new();
    private int _disposeCount = 0;

    // Centurion feature IDs
    private const ushort FEAT_FEATURE_SET = 0x0001;
    private const ushort FEAT_CENTPP_BRIDGE = 0x0003;
    private const ushort FEAT_DEVICE_INFO = 0x0100;
    private const ushort FEAT_DEVICE_NAME = 0x0101;
    private const ushort FEAT_BATTERY_SOC = 0x0104;

    public CenturionDevice(HidDevicePtr dev, ushort usagePage)
    {
        _transport = new CenturionTransport(dev);
        _usagePage = usagePage;
    }

    public async Task InitAsync()
    {
        try
        {
            DiagnosticLogger.Log($"{Tag} Starting feature discovery...");

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
            _bridgeIdx = await QueryFeatureIndex(FEAT_CENTPP_BRIDGE);
            _deviceInfoIdx = await QueryFeatureIndex(FEAT_DEVICE_INFO);
            byte directBatterySocIdx = await QueryFeatureIndex(FEAT_BATTERY_SOC);
            _deviceNameIdx = await QueryFeatureIndex(FEAT_DEVICE_NAME);

            if (_bridgeIdx != 0xFF)
            {
                // DONGLE MODE: headset may be asleep — defer registration until reachable.
                // We start the background loops immediately so bridge events are not missed.
                _isDongleMode = true;
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
                _isDongleMode = false;
                _batterySocIdx = directBatterySocIdx;
                DiagnosticLogger.Log($"{Tag} Direct mode — BatterySOC at index {_batterySocIdx}");

                await TryGetDeviceName();
                await TryGetDeviceInfo();
                
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

        bool initCompleted = false;
        await _ioLock.WaitAsync(_cts.Token);
        try
        {
            if (!_pendingInit) return; // Double-check

            // Step 1: Contact headset via CentPPBridge.getConnectionInfo (func 0)
            var connResp = await SendRequestWithResponseAsync(_bridgeIdx, 0x00, []);
            if (connResp != null && !connResp.Value.HeadsetOffline)
            {
                DiagnosticLogger.Log($"{Tag} Headset connected via bridge");

                // Step 2: Discover sub-device features via bridge
                // We route CenturionRoot.getFeature() through the bridge envelope.
                _batterySocIdx = await QueryFeatureIndexViaBridge(FEAT_BATTERY_SOC);
                byte subNameIdx = await QueryFeatureIndexViaBridge(FEAT_DEVICE_NAME);
                if (subNameIdx != 0xFF) _deviceNameIdx = subNameIdx;
                byte subInfoIdx = await QueryFeatureIndexViaBridge(FEAT_DEVICE_INFO);
                if (subInfoIdx != 0xFF) _deviceInfoIdx = subInfoIdx;

                if (_batterySocIdx != 0xFF)
                {
                    // Step 3: Fetch metadata (name, HW info, serial)
                    await TryGetDeviceName();
                    await TryGetDeviceInfo();
                    initCompleted = true;
                }
            }
        }
        finally
        {
            _ioLock.Release();
        }

        if (initCompleted)
        {
            // Step 4: Compute identifier and register with UI
            _identifier = DeviceIdentifierGenerator.GenerateIdentifier(_serialNumber, _unitId, _modelId, _deviceName);
            string deviceSignature = $"NATIVE.{DeviceType.Headset}.{_identifier}";
            
            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.INIT, 
                new InitMessage(_identifier, _deviceName, true, DeviceType.Headset, deviceSignature)
            );
            
            DiagnosticLogger.Log($"{Tag} Device registered: {_deviceName} ({_identifier})");

            // Step 5: Clear pending flag and do first battery read
            _pendingInit = false;
            await UpdateBattery(forceUpdate: true);
        }
    }

    /// <summary>
    /// Processes frames as they arrive, invoking event handlers for unsolicited events
    /// and completing pending requests for matching responses.     
    /// </summary>    
    private async Task ResponseDispatcherAsync(CancellationToken ct)
    {
        DiagnosticLogger.Log($"{Tag} Dispatcher started");
        try
        {
            await foreach (var frame in _frameChannel.Reader.ReadAllAsync(ct))
            {
                // 1. Handle Unsolicited Events (SwId == 0x00)
                if (frame.SwId == 0x00)
                {
                    await HandleAsyncEvent(frame);
                    continue;
                }

                // 2. Handle Request Responses (SwId == SWID)
                if (frame.SwId == CenturionTransport.SWID)
                {
                    // Outer ACK for CentPPBridge.sendMessage (func=0x01) — this is just the
                    // dongle confirming it forwarded the message. The headset's actual response
                    // arrives as a MessageEvent with swid=0 caught above.
                    if (_isDongleMode && frame.FeatIdx == _bridgeIdx && frame.FuncId == 0x01)
                    {
                        DiagnosticLogger.Verbose($"{Tag} Dispatcher: bridge sendMessage ACK (ignored, awaiting MessageEvent)");
                        continue;
                    }

                    if (_pendingRequests.TryRemove(frame.SwId, out var tcs))
                    {
                        tcs.TrySetResult(frame);
                    }
                    continue;
                }

                // 3. Handle Unexpected frames
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

    private async Task HandleAsyncEvent(CenturionResponse frame)
    {
        // Battery event: BatterySOC feature, swid=0 (unsolicited)
        if (frame.FeatIdx == _batterySocIdx && frame.FuncId == 0x00) // Usually func 0 for unsolicited
        {
            var batState = ParseBatterySOC(frame.Params);
            if (batState != null && !string.IsNullOrEmpty(_identifier))
            {
                _batteryPublisher.PublishUpdate(_identifier, _deviceName, batState.Value, DateTimeOffset.Now, "event");
            }
            return;
        }

        // Dongle mode: watch for bridge connection state changes
        if (_isDongleMode && frame.FeatIdx == _bridgeIdx)
        {            
            switch (frame.FuncId)
            {
                case 0x00: // ConnectionStateChanged (event)
                    await HandleBridgeConnectionEvent(frame);
                    break;

                case 0x01: // MessageEvent (wrapped sub-device response)                    
                    var subFrame = CenturionTransport.UnwrapBridgePayload(frame.Params);
                    if (subFrame != null)
                    {
                        // Solicited response: inner swid matches ours — complete the pending bridge request
                        if (subFrame.Value.SwId == CenturionTransport.SWID &&
                            _pendingRequests.TryRemove(subFrame.Value.SwId, out var tcs))
                        {
                            tcs.TrySetResult(subFrame.Value);
                        }
                        else
                        {
                            // Unsolicited sub-device event (swid=0) — recurse for battery/connection handling
                            await HandleAsyncEvent(subFrame.Value);
                        }
                    }
                    break;
            }            
        }
    }

    private async Task HandleBridgeConnectionEvent(CenturionResponse resp)
    {        
        if (resp.HeadsetOnline)
        {
            DiagnosticLogger.Log($"{Tag} Bridge: headset connected");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // Wait for RF stability
                if (_pendingInit) await CompleteInitAsync();
                else await UpdateBattery(forceUpdate: true);
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

    // ---- I/O Orchestration ----

    /// <summary>
    /// Sends a request to the transport layer and asynchronously waits for a response or a timeout.
    /// Ensures that only one request is processed at a time.
    /// </summary>    
    private async Task<CenturionResponse?> SendRequestWithResponseAsync(byte featIdx, byte func, byte[] parameters, int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource<CenturionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[CenturionTransport.SWID] = tcs;

        try
        {
            await _ioLock.WaitAsync(_cts.Token);
            try
            {
                await _transport.SendRequest(featIdx, func, parameters);
            }
            finally
            {
                _ioLock.Release();
            }

            using var delayCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, delayCts.Token);
            
            return await tcs.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(CenturionTransport.SWID, out _);
        }
    }

    private async Task<CenturionResponse?> SendBridgeRequestWithResponseAsync(byte subFeatIdx, byte subFunc, byte[] subParams, int timeoutMs = 3000)
    {
        // Two-phase bridge response:
        //   1. Outer ACK  (swid=ours, feat=bridgeIdx, func=0x01) — dongle confirms it forwarded the
        //      message; dispatcher ignores this frame.
        //   2. MessageEvent (swid=0, feat=bridgeIdx, func=0x01) — dongle delivers the headset's reply;
        //      dispatcher unwraps the inner sub-frame, sees swid=0x0A (embedded by SendBridgeRequest),
        //      and completes this TCS with the real headset response data.
        var tcs = new TaskCompletionSource<CenturionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[CenturionTransport.SWID] = tcs;

        try
        {
            await _ioLock.WaitAsync(_cts.Token);
            try
            {
                await _transport.SendBridgeRequest(_bridgeIdx, _subDeviceId, subFeatIdx, subFunc, subParams);
            }
            finally
            {
                _ioLock.Release();
            }

            using var delayCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, delayCts.Token);

            return await tcs.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(CenturionTransport.SWID, out _);
        }
    }



    /// <summary>
    /// Query CenturionRoot (index 0, func 0 = getFeature) for a feature's index.
    /// Returns 0xFF if not found.
    /// </summary>
    private async Task<byte> QueryFeatureIndex(ushort featureId)
    {
        byte[] featureIdBytes = [(byte)(featureId >> 8), (byte)(featureId & 0xFF)];
        var resp = await SendRequestWithResponseAsync(0x00, 0x00, featureIdBytes);
        if (resp == null || resp.Value.Params.Length == 0) return 0xFF;
        byte index = resp.Value.Params[0];
        if (index == 0 && featureId != 0x0000) return 0xFF;
        return index;
    }

    /// <summary>
    /// Query a feature index on the sub-device via CentPPBridge.
    /// Routes CenturionRoot.getFeature() through the bridge envelope.
    /// </summary>
    private async Task<byte> QueryFeatureIndexViaBridge(ushort featureId)
    {
        byte[] featureIdBytes = [(byte)(featureId >> 8), (byte)(featureId & 0xFF)];
        var resp = await SendBridgeRequestWithResponseAsync(0x00, 0x00, featureIdBytes);
        if (resp == null || resp.Value.Params.Length == 0) return 0xFF;
        byte index = resp.Value.Params[0];
        if (index == 0 && featureId != 0x0000) return 0xFF;
        return index;
    }

    /// <summary>
    /// Reads the device name via the DeviceName feature.
    /// Supports both inline name response and chunked fallback.
    /// </summary>
    private async Task TryGetDeviceName()
    {
        if (_deviceNameIdx == 0xFF) return;
        try
        {
            // DeviceName func 0 = getNameLength, func 1 = getNameChunk
            var lengthResp = _isDongleMode 
                ? await SendBridgeRequestWithResponseAsync(_deviceNameIdx, 0x00, [])
                : await SendRequestWithResponseAsync(_deviceNameIdx, 0x00, []);

            if (lengthResp == null || lengthResp.Value.Params.Length == 0) return;
            int nameLen = lengthResp.Value.Params[0];
            if (nameLen == 0 || nameLen > 64) return;

            // --- INLINE NAME SUPPORT ---
            // Some Centurion devices return the full name in the first response if it fits.
            if (lengthResp.Value.Params.Length >= 1 + nameLen)
            {
                _deviceName = System.Text.Encoding.UTF8.GetString(lengthResp.Value.Params, 1, nameLen).TrimEnd('\0');
                DiagnosticLogger.Log($"{Tag} Device name (inline): {_deviceName}");
                return;
            }

            // --- CHUNKED NAME FALLBACK ---
            // Otherwise, fetch name in chunks via function 1 (standard HID++ 2.0 behavior)
            var nameBytes = new List<byte>();
            for (int offset = 0; offset < nameLen; offset += 16)
            {
                var chunkResp = _isDongleMode
                    ? await SendBridgeRequestWithResponseAsync(_deviceNameIdx, 0x01, [(byte)offset])
                    : await SendRequestWithResponseAsync(_deviceNameIdx, 0x01, [(byte)offset]);

                if (chunkResp == null || chunkResp.Value.Params.Length == 0) break;
                nameBytes.AddRange(chunkResp.Value.Params);
            }
            if (nameBytes.Count > 0)
            {
                _deviceName = System.Text.Encoding.UTF8.GetString(nameBytes.ToArray(), 0, Math.Min(nameLen, nameBytes.Count)).TrimEnd('\0');
                DiagnosticLogger.Log($"{Tag} Device name (chunked): {_deviceName}");
            }
        } catch { }
    }

    /// <summary>
    /// Reads hardware info (model, revision) and serial number via DeviceInfo (0x0100).
    /// </summary>
    private async Task TryGetDeviceInfo()
    {
        if (_deviceInfoIdx == 0xFF) return;
        try
        {
            // func 0 = getHardwareInfo (modelId, hwRevision, productId)
            var hwResp = _isDongleMode
                ? await SendBridgeRequestWithResponseAsync(_deviceInfoIdx, 0x00, [])
                : await SendRequestWithResponseAsync(_deviceInfoIdx, 0x00, []);

            if (hwResp != null && hwResp.Value.Params.Length >= 4)
            {
                _modelId = hwResp.Value.Params[0].ToString("X2");
                _unitId = hwResp.Value.Params[1].ToString("X2"); // HW revision
                DiagnosticLogger.Log($"{Tag} HW Info: Model={_modelId}, Rev={_unitId}");
            }

            // func 2 = getSerialNumber
            var snResp = _isDongleMode
                ? await SendBridgeRequestWithResponseAsync(_deviceInfoIdx, 0x02, [])
                : await SendRequestWithResponseAsync(_deviceInfoIdx, 0x02, []);

            if (snResp != null && snResp.Value.Params.Length >= 1)
            {
                int snLen = snResp.Value.Params[0];
                if (snLen > 0 && snResp.Value.Params.Length >= 1 + snLen)
                {
                    _serialNumber = System.Text.Encoding.ASCII.GetString(snResp.Value.Params, 1, snLen).TrimEnd('\0');
                    DiagnosticLogger.Log($"{Tag} Serial Number: {_serialNumber}");
                }
            }
        } catch { }
    }

    private async Task<bool> UpdateBattery(bool forceUpdate = false)
    {
        if (string.IsNullOrEmpty(_identifier) || _batterySocIdx == 0xFF) return false;
        try
        {
            var resp = _isDongleMode
                ? await SendBridgeRequestWithResponseAsync(_batterySocIdx, 0x00, [])
                : await SendRequestWithResponseAsync(_batterySocIdx, 0x00, []);

            if (resp == null) return false;
            var batState = ParseBatterySOC(resp.Value.Params);
            if (batState == null) return false;

            _batteryPublisher.PublishUpdate(_identifier, _deviceName, batState.Value, DateTimeOffset.Now, "poll", forceUpdate);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Parse BatterySOC (0x0104) response params.
    /// Byte 0: SOC percentage (0-100)
    /// Byte 1: SOC percentage (duplicate)
    /// Byte 2: Charging status (0=discharging, 1=charging, 2=USB charging, 3=full)
    /// </summary>
    private static BatteryUpdateReturn? ParseBatterySOC(byte[] parameters)
    {
        if (parameters.Length < 3)
        {
            DiagnosticLogger.LogWarning($"[Centurion] BatterySOC response too short ({parameters.Length} bytes)");
            return null;
        }
        
        byte soc = Math.Min((byte)100, parameters[0]);
        var status = parameters[2] switch { 
            0 => PowerSupplyStatus.DISCHARGING, 
            1 or 2 => PowerSupplyStatus.CHARGING,
            3 => PowerSupplyStatus.FULL,
            _ => PowerSupplyStatus.UNKNOWN 
        };

        DiagnosticLogger.Log($"[Centurion] Battery: {soc}% status={parameters[2]} → {status}");
        return new BatteryUpdateReturn(soc, status, 0);
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
        if (Interlocked.Increment(ref _disposeCount) != 1) return;
        
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

        _transport.Dispose();
        _ioLock.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
