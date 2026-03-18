using LGSTrayHID.Battery;
using LGSTrayHID.Features;
using LGSTrayHID.HidApi;
using LGSTrayHID.Metadata;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;

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

    // Device state
    private bool _isDongleMode;
    private byte _subDeviceId = 0x01;   // Default sub-device ID for bridge mode
    private string _deviceName = "Centurion Headset";
    private string _identifier = string.Empty;

    // True while dongle is detected but headset has not yet been contacted.
    // No InitMessage is sent until this is cleared by CompleteInitAsync.
    private volatile bool _pendingInit = false;

    // Battery polling
    private readonly BatteryUpdatePublisher _batteryPublisher = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private Task? _pollingTask;
    private Task? _readLoopTask;

    private int _disposeCount = 0;

    // Centurion feature IDs
    private const ushort FEAT_FEATURE_SET = 0x0001;
    private const ushort FEAT_CENTPP_BRIDGE = 0x0003;
    private const ushort FEAT_DEVICE_INFO = 0x0100;
    private const ushort FEAT_DEVICE_NAME = 0x0101;
    private const ushort FEAT_BATTERY_SOC = 0x0104;

    public CenturionDevice(HidDevicePtr dev, ushort usagePage, byte reportId = 0x50)
    {
        _transport = new CenturionTransport(dev, reportId);
        _usagePage = usagePage;
    }

    public async Task InitAsync()
    {
        try
        {
            DiagnosticLogger.Log($"{Tag} Starting feature discovery...");

            // Step 1: Discover parent features via CenturionRoot (index 0, func 0)
            _bridgeIdx = await QueryFeatureIndex(FEAT_CENTPP_BRIDGE);
            byte directBatterySocIdx = await QueryFeatureIndex(FEAT_BATTERY_SOC);
            _deviceNameIdx = await QueryFeatureIndex(FEAT_DEVICE_NAME);

            if (_bridgeIdx != 0xFF)
            {
                // DONGLE MODE: headset may be asleep — defer registration until reachable
                _isDongleMode = true;
                _pendingInit = true;
                DiagnosticLogger.Log($"{Tag} Dongle mode — CentPPBridge at index {_bridgeIdx}");

                // Start background loops BEFORE CompleteInitAsync so bridge events
                // that arrive during (or shortly after) the first contact attempt
                // are not missed.
                _readLoopTask = BackgroundReadLoopAsync(_cts.Token);
                _pollingTask = Task.Run(() => PollBattery(_cts.Token), _cts.Token);

                // Attempt immediate registration — succeeds if headset already awake.
                // If headset is sleeping, _pendingInit stays true and the loops will retry.
                await CompleteInitAsync();
            }
            else if (directBatterySocIdx != 0xFF)
            {
                // DIRECT MODE: device is always reachable, register immediately
                _isDongleMode = false;
                _batterySocIdx = directBatterySocIdx;
                DiagnosticLogger.Log($"{Tag} Direct mode — BatterySOC at index {_batterySocIdx}");

                await TryGetDeviceName();
                _identifier = DeviceIdentifierGenerator.GenerateIdentifier(null, null, null, _deviceName);
                string deviceSignature = $"NATIVE.{DeviceType.Headset}.{_identifier}";

                HidppManagerContext.Instance.SignalDeviceEvent(
                    IPCMessageType.INIT,
                    new InitMessage(_identifier, _deviceName, hasBattery: true, DeviceType.Headset, deviceSignature)
                );
                DiagnosticLogger.Log($"{Tag} Device registered: {_deviceName} ({_identifier})");

                await UpdateBattery(forceUpdate: true);

                _readLoopTask = BackgroundReadLoopAsync(_cts.Token);
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
    /// Holds _ioLock across all HID I/O (InitDongleMode + TryGetDeviceName).
    /// </summary>
    private async Task CompleteInitAsync()
    {
        if (!_pendingInit)
            return;

        // --- Step 1: Contact headset and discover battery/name feature indices ---
        bool reached;
        await _ioLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            reached = await InitDongleMode();
            if (reached)
                await TryGetDeviceName();
        }
        finally
        {
            _ioLock.Release();
        }

        if (!reached)
        {
            DiagnosticLogger.Log($"{Tag} CompleteInitAsync: headset not reachable, staying pending");
            return;
        }

        // --- Step 2: Compute identifier from real device name ---
        _identifier = DeviceIdentifierGenerator.GenerateIdentifier(null, null, null, _deviceName);
        string deviceSignature = $"NATIVE.{DeviceType.Headset}.{_identifier}";

        // --- Step 3: Register with UI ---
        HidppManagerContext.Instance.SignalDeviceEvent(
            IPCMessageType.INIT,
            new InitMessage(_identifier, _deviceName, hasBattery: true, DeviceType.Headset, deviceSignature)
        );
        DiagnosticLogger.Log($"{Tag} Device registered: {_deviceName} ({_identifier})");

        // --- Step 4: Clear pending flag (must happen before UpdateBattery) ---
        _pendingInit = false;

        // --- Step 5: First battery read ---
        await UpdateBattery(forceUpdate: true);
    }

    // ---- Dongle mode initialization ----

    /// <summary>
    /// Contacts the headset via CentPPBridge and discovers battery/name feature indices.
    /// Returns true if the headset is connected and BatterySOC was found; false if sleeping.
    /// Must be called under _ioLock.
    /// </summary>
    private async Task<bool> InitDongleMode()
    {
        // Check if headset is connected via CentPPBridge.getConnectionInfo (func 0)
        await _transport.SendRequest(_bridgeIdx, func: 0x00, parameters: []);
        var connResp = _transport.ReadResponse(timeoutMs: 2000);

        if (connResp == null)
        {
            DiagnosticLogger.LogWarning($"{Tag} No response from CentPPBridge.getConnectionInfo");
            return false;
        }

        // All-zero params means no sub-device connected (headset sleeping or absent)
        if (connResp.Value.Params.Length > 0 && connResp.Value.Params.All(b => b == 0))
        {
            DiagnosticLogger.LogWarning($"{Tag} Headset appears offline (bridge reports no connected device)");
            return false;
        }

        DiagnosticLogger.Log($"{Tag} Headset connected via bridge");

        // Discover BatterySOC on sub-device via bridge
        _batterySocIdx = await QueryFeatureIndexViaBridge(FEAT_BATTERY_SOC);

        if (_batterySocIdx == 0xFF)
        {
            DiagnosticLogger.LogWarning($"{Tag} Sub-device does not expose BatterySOC (0x0104)");
            return false;
        }

        DiagnosticLogger.Log($"{Tag} Sub-device BatterySOC at index {_batterySocIdx}");

        // Also try to get device name from sub-device
        byte subNameIdx = await QueryFeatureIndexViaBridge(FEAT_DEVICE_NAME);
        if (subNameIdx != 0xFF)
        {
            _deviceNameIdx = subNameIdx; // Prefer sub-device name over dongle name
        }

        return true;
    }

    // ---- Feature discovery ----

    /// <summary>
    /// Query CenturionRoot (index 0, func 0 = getFeature) for a feature's index.
    /// Returns 0xFF if not found.
    /// </summary>
    private async Task<byte> QueryFeatureIndex(ushort featureId)
    {
        byte[] featureIdBytes = [(byte)(featureId >> 8), (byte)(featureId & 0xFF)];
        await _transport.SendRequest(featIdx: 0x00, func: 0x00, parameters: featureIdBytes);

        var resp = _transport.ReadResponse(timeoutMs: 2000);
        if (resp == null || resp.Value.Params.Length == 0)
            return 0xFF;

        byte index = resp.Value.Params[0];

        // Index 0 typically means "not found" for non-root features
        if (index == 0 && featureId != 0x0000)
        {
            DiagnosticLogger.Verbose($"[Centurion] Feature 0x{featureId:X4} not found (index=0)");
            return 0xFF;
        }

        DiagnosticLogger.Log($"[Centurion] Feature 0x{featureId:X4} → index {index}");
        return index;
    }

    /// <summary>
    /// Query a feature index on the sub-device via CentPPBridge.
    /// Routes CenturionRoot.getFeature() through the bridge envelope.
    /// Must be called under _ioLock.
    /// </summary>
    private async Task<byte> QueryFeatureIndexViaBridge(ushort featureId)
    {
        byte[] featureIdBytes = [(byte)(featureId >> 8), (byte)(featureId & 0xFF)];

        await _transport.SendBridgeRequest(
            bridgeIdx: _bridgeIdx,
            devId: _subDeviceId,
            subFeatIdx: 0x00,     // CenturionRoot on sub-device
            subFunc: 0x00,        // getFeature
            subParams: featureIdBytes
        );

        var resp = _transport.ReadBridgeResponse(_bridgeIdx, timeoutMs: 3000);
        if (resp == null || resp.Value.Params.Length == 0)
            return 0xFF;

        byte index = resp.Value.Params[0];
        if (index == 0 && featureId != 0x0000)
        {
            DiagnosticLogger.Verbose($"[Centurion] Sub-device feature 0x{featureId:X4} not found");
            return 0xFF;
        }

        DiagnosticLogger.Log($"[Centurion] Sub-device feature 0x{featureId:X4} → index {index}");
        return index;
    }

    // ---- Device name ----

    /// <summary>
    /// Reads the device name via the DeviceName feature.
    /// Must be called under _ioLock in dongle mode.
    /// </summary>
    private async Task TryGetDeviceName()
    {
        if (_deviceNameIdx == 0xFF)
            return;

        try
        {
            // DeviceName func 0 = getNameLength, func 1 = getNameChunk
            CenturionResponse? lengthResp;

            if (_isDongleMode)
            {
                await _transport.SendBridgeRequest(_bridgeIdx, _subDeviceId, _deviceNameIdx, 0x00, []);
                lengthResp = _transport.ReadBridgeResponse(_bridgeIdx, timeoutMs: 3000);
            }
            else
            {
                await _transport.SendRequest(_deviceNameIdx, 0x00, []);
                lengthResp = _transport.ReadResponse(timeoutMs: 2000);
            }

            if (lengthResp == null || lengthResp.Value.Params.Length == 0)
                return;

            int nameLen = lengthResp.Value.Params[0];
            if (nameLen == 0 || nameLen > 64)
                return;

            // Read name in chunks
            var nameBytes = new List<byte>();
            for (int offset = 0; offset < nameLen; offset += 16)
            {
                CenturionResponse? chunkResp;

                if (_isDongleMode)
                {
                    await _transport.SendBridgeRequest(_bridgeIdx, _subDeviceId, _deviceNameIdx, 0x01, [(byte)offset]);
                    chunkResp = _transport.ReadBridgeResponse(_bridgeIdx, timeoutMs: 3000);
                }
                else
                {
                    await _transport.SendRequest(_deviceNameIdx, 0x01, [(byte)offset]);
                    chunkResp = _transport.ReadResponse(timeoutMs: 2000);
                }

                if (chunkResp == null || chunkResp.Value.Params.Length == 0)
                    break;

                nameBytes.AddRange(chunkResp.Value.Params);
            }

            if (nameBytes.Count > 0)
            {
                // Trim to actual length and strip null terminators
                int actualLen = Math.Min(nameLen, nameBytes.Count);
                string name = System.Text.Encoding.UTF8.GetString(nameBytes.ToArray(), 0, actualLen).TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _deviceName = name;
                    DiagnosticLogger.Log($"{Tag} Device name: {_deviceName}");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"{Tag} Failed to read device name: {ex.Message}");
        }
    }

    // ---- Battery ----

    private async Task<bool> UpdateBattery(bool forceUpdate = false)
    {
        // Cannot publish without a registered identifier
        if (string.IsNullOrEmpty(_identifier))
            return false;

        // Lazy discovery: headset may have been asleep at startup.
        if (_batterySocIdx == 0xFF)
        {
            if (!_isDongleMode)
                return false;

            await _ioLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                _batterySocIdx = await QueryFeatureIndexViaBridge(FEAT_BATTERY_SOC);
            }
            finally
            {
                _ioLock.Release();
            }

            if (_batterySocIdx == 0xFF)
                return false;
        }

        try
        {
            CenturionResponse? resp;

            await _ioLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (_isDongleMode)
                {
                    await _transport.SendBridgeRequest(_bridgeIdx, _subDeviceId, _batterySocIdx, 0x00, []);
                    resp = _transport.ReadBridgeResponse(_bridgeIdx, timeoutMs: 3000);
                }
                else
                {
                    await _transport.SendRequest(_batterySocIdx, 0x00, []);
                    resp = _transport.ReadResponse(timeoutMs: 2000);
                }
            }
            finally
            {
                _ioLock.Release();
            }

            if (resp == null)
            {
                DiagnosticLogger.LogWarning("[Centurion] Battery query returned no response");
                return false;
            }

            var batState = ParseBatterySOC(resp.Value.Params);
            if (batState == null)
                return false;

            var now = DateTimeOffset.Now;
            _batteryPublisher.PublishUpdate(_identifier, _deviceName, batState.Value, now, "poll", forceUpdate);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"[Centurion] Battery update failed: {ex.Message}");
            return false;
        }
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

        byte soc = parameters[0];
        byte chargingStatus = parameters[2];

        // Clamp SOC to valid range
        if (soc > 100)
        {
            DiagnosticLogger.LogWarning($"[Centurion] BatterySOC out of range: {soc}%, clamping to 100");
            soc = 100;
        }

        var status = chargingStatus switch
        {
            0 => PowerSupplyStatus.DISCHARGING,
            1 => PowerSupplyStatus.CHARGING,
            2 => PowerSupplyStatus.CHARGING,    // USB charging → charging
            3 => PowerSupplyStatus.FULL,
            _ => PowerSupplyStatus.UNKNOWN,
        };

        DiagnosticLogger.Log($"[Centurion] Battery: {soc}% status={chargingStatus} → {status}");
        return new BatteryUpdateReturn(soc, status, batteryMVolt: 0);
    }

    // ---- Polling ----

    private async Task PollBattery(CancellationToken ct)
    {
        int intervalSeconds = GetPollInterval();
        DiagnosticLogger.Log($"[Centurion] Battery polling started (interval: {intervalSeconds}s)");

        try
        {
            // Initial delay before first poll (CompleteInitAsync already did first read if registered)
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);

            while (!ct.IsCancellationRequested)
            {
                string tag = $"[Centurion 0x{_usagePage:X4}]";

                if (_pendingInit)
                {
                    // Headset not yet contacted — retry full registration
                    DiagnosticLogger.Log("[Centurion] Poll tick: device still pending, retrying init");
                    await CompleteInitAsync();
                }
                else
                {
                    await UpdateBattery();
                }

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        DiagnosticLogger.Log("[Centurion] Battery polling stopped");
    }

    private static int GetPollInterval()
    {
#if DEBUG
        return 30;
#else
        return Math.Clamp(GlobalSettings.settings.PollPeriod, 20, 3600);
#endif
    }

    // ---- Background read loop for unsolicited events ----

    // Async loop so it can yield the _ioLock between reads, allowing UpdateBattery
    // to interleave without both sides calling _dev.Read() concurrently.
    private async Task BackgroundReadLoopAsync(CancellationToken ct)
    {
        DiagnosticLogger.Log("[Centurion] Background read loop started");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                CenturionResponse? frame;

                await _ioLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Short timeout so the lock is released frequently, letting
                    // UpdateBattery acquire it without a long wait.
                    frame = _transport.ReadResponse(timeoutMs: 100);
                }
                finally
                {
                    _ioLock.Release();
                }

                if (frame == null)
                    continue;

                // Battery event: BatterySOC feature, swid=0 (unsolicited)
                if (frame.Value.FeatIdx == _batterySocIdx && frame.Value.SwId == 0x00)
                {
                    var batState = ParseBatterySOC(frame.Value.Params);
                    if (batState != null && !string.IsNullOrEmpty(_identifier))
                    {
                        var now = DateTimeOffset.Now;
                        _batteryPublisher.PublishUpdate(_identifier, _deviceName, batState.Value, now, "event");
                    }
                }

                // Dongle mode: watch for bridge connection state changes
                if (_isDongleMode && frame.Value.FeatIdx == _bridgeIdx && frame.Value.SwId == 0x00)
                {
                    DiagnosticLogger.Log($"[Centurion] Bridge event received: func={frame.Value.FuncId} " +
                                         $"params={BitConverter.ToString(frame.Value.Params)}");

                    // ConnectionStateChanged (func=0)
                    if (frame.Value.FuncId == 0x00)
                    {
                        // Same convention as getConnectionInfo: all-zero params = no sub-device
                        bool headsetConnected = frame.Value.Params.Length > 0
                                                && !frame.Value.Params.All(b => b == 0);

                        if (headsetConnected)
                        {
                            DiagnosticLogger.Log("[Centurion] Bridge: headset connected");
                            string tag = $"[Centurion 0x{_usagePage:X4}]";
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    // Allow RF link to stabilise before querying features
                                    await Task.Delay(1000, ct);

                                    if (_pendingInit)
                                        await CompleteInitAsync();
                                    else
                                        await UpdateBattery(forceUpdate: true);
                                }
                                catch (OperationCanceledException) { }
                            }, ct);
                        }
                        else
                        {
                            DiagnosticLogger.Log("[Centurion] Bridge: headset disconnected");

                            // Only send offline notification if device was actually registered
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
                                DiagnosticLogger.Log($"[Centurion] Offline notification sent for {_deviceName}");
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        DiagnosticLogger.Log("[Centurion] Background read loop ended");
    }

    // ---- Disposal ----

    public void Dispose()
    {
        if (Interlocked.Increment(ref _disposeCount) != 1)
            return;

        DiagnosticLogger.Log($"[Centurion] Disposing {_deviceName}");

        _cts.Cancel();

        // Wait briefly for tasks to exit
        try { _pollingTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* expected */ }
        try { _readLoopTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* expected */ }

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
