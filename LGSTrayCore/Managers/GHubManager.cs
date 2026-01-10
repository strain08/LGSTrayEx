using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using Websocket.Client;

namespace LGSTrayCore.Managers;

file struct GHUBMsg
{
    public string MsgId { get; set; }
    public string Verb { get; set; }
    public string Path { get; set; }
    public string Origin { get; set; }
    public JObject Result { get; set; }
    public JObject Payload { get; set; }

    public static GHUBMsg? DeserializeJson(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                DiagnosticLogger.LogWarning("GHUB received null or empty message");
                return null;
            }

            return JsonConvert.DeserializeObject<GHUBMsg>(json);
        }
        catch (JsonException ex)
        {
            DiagnosticLogger.LogError($"GHUB JSON deserialization failed: {ex.Message}");
            // Log first 500 chars of the raw message for debugging
            int maxLength = Math.Min(500, json?.Length ?? 0);
            DiagnosticLogger.LogError($"Raw message (truncated): {json?.Substring(0, maxLength)}");
            return null;
        }
    }
}

public partial class GHubManager : IDeviceManager, IHostedService, IDisposable
{
    #region IDisposable
    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // Dispose WebSocket event subscriptions to prevent memory leaks
                _messageSubscription?.Dispose();
                _messageSubscription = null;

                _disconnectionSubscription?.Dispose();
                _disconnectionSubscription = null;

                _reconnectionSubscription?.Dispose();
                _reconnectionSubscription = null;

                // Dispose WebSocket client
                _ws?.Dispose();
                _ws = null;
            }

            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~GHubManager()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion
    // Time out for reconnect attempts (ms)
    const double RECONNECT_TIMEOUT = 15000;

    private const string WEBSOCKET_SERVER = "ws://localhost:9010";

    [GeneratedRegex(@"\/battery\/dev[0-9a-zA-Z]+\/state")]
    private static partial Regex BatteryDeviceStateRegex();

    private readonly IPublisher<IPCMessage> _deviceEventBus;
    private readonly IWebSocketClientFactory _wsFactory;

    protected IWebSocketClient? _ws;

    // WebSocket event subscriptions (must be disposed to prevent memory leaks)
    private IDisposable? _messageSubscription;
    private IDisposable? _disconnectionSubscription;
    private IDisposable? _reconnectionSubscription;

    // Protocol change detection
    private int _protocolErrorCount = 0;
    private const int PROTOCOL_ERROR_THRESHOLD = 3;
    private DateTime _lastProtocolErrorNotification = DateTime.MinValue;

    public GHubManager(IPublisher<IPCMessage> deviceEventBus, IWebSocketClientFactory wsFactory)
    {
        _deviceEventBus = deviceEventBus;
        _wsFactory = wsFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var url = new Uri(WEBSOCKET_SERVER);
        _ws = _wsFactory.Create(url);

        // Store subscriptions so they can be disposed later (prevents memory leaks)
        _messageSubscription = _ws.MessageReceived.Subscribe(ParseSocketMsg);

        _ws.ErrorReconnectTimeout = TimeSpan.FromMilliseconds(RECONNECT_TIMEOUT);
        _ws.ReconnectTimeout = null;
        _disconnectionSubscription = _ws.DisconnectionHappened.Subscribe(info =>
        {
            DiagnosticLogger.LogWarning($"GHUB WebSocket disconnected: {info.Type}");
            DiagnosticLogger.Log("Clearing all GHUB devices.");
            _deviceEventBus.Publish(new RemoveMessage("*GHUB*", "rediscover_cleanup"));
        });
        _reconnectionSubscription = _ws.ReconnectionHappened.Subscribe(info =>
        {
            DiagnosticLogger.Log("GHUB WebSocket reconnected, reloading devices");
            if (info.Type != ReconnectionType.Initial) _ = RediscoverDevices();
        });

        DiagnosticLogger.Log($"Attempting to connect to LGHUB at {url}");
        try
        {
            await _ws.Start();
        }
        catch (Websocket.Client.Exceptions.WebsocketException ex)
        {
            DiagnosticLogger.LogError($"Failed to connect to GHUB: {ex.Message}");
            this.Dispose();
            return;
        }

        DiagnosticLogger.Log("Connected to GHUB successfully");

        _ws.Send(JsonConvert.SerializeObject(new
        {
            msgId = "",
            verb = "SUBSCRIBE",
            path = "/devices/state/changed"
        }));

        _ws.Send(JsonConvert.SerializeObject(new
        {
            msgId = "",
            verb = "SUBSCRIBE",
            path = "/battery/state/changed"
        }));

        DiagnosticLogger.Log("Requesting device list from GHUB");
        LoadDevices();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Dispose subscriptions before disposing WebSocket client
        _messageSubscription?.Dispose();
        _messageSubscription = null;

        _disconnectionSubscription?.Dispose();
        _disconnectionSubscription = null;

        _reconnectionSubscription?.Dispose();
        _reconnectionSubscription = null;

        _ws?.Dispose();
        _ws = null;

        return Task.CompletedTask;
    }

    public void LoadDevices()
    {
        _ws?.Send(JsonConvert.SerializeObject(new
        {
            msgId = "",
            verb = "GET",
            path = "/devices/list"
        }));
    }

    protected void ParseSocketMsg(ResponseMessage msg)
    {
        GHUBMsg? ghubmsgNullable = GHUBMsg.DeserializeJson(msg.Text);

        if (ghubmsgNullable == null)
        {
            DiagnosticLogger.LogWarning("GHUB Failed to deserialize message - possible protocol change");
            return;
        }

        // Extract non-nullable value
        GHUBMsg ghubmsg = ghubmsgNullable.Value;

        DiagnosticLogger.Log($"GHUB message received - Path: {ghubmsg.Path}");
        //DiagnosticLogger.Log($"Full message: {msg.Text}");

        switch (ghubmsg.Path)
        {
            case "/devices/list":
                {
                    DiagnosticLogger.Log("GHUB Processing /devices/list response");
                    if (ghubmsg.Payload == null)
                    {
                        DiagnosticLogger.LogWarning("GHUB Received /devices/list with null payload");
                        break;
                    }
                    ParseDevicesList(ghubmsg.Payload);
                    break;
                }
            case "/battery/state/changed":
            case { } when BatteryDeviceStateRegex().IsMatch(ghubmsg.Path):
                {
                    DiagnosticLogger.Log($"GHUB Processing battery update: {ghubmsg.Path}");
                    if (ghubmsg.Payload == null)
                    {
                        DiagnosticLogger.LogWarning($"GHUB Received battery update with null payload: {ghubmsg.Path}");
                        break;
                    }
                    ParseBatteryUpdate(ghubmsg.Payload);
                    break;
                }
            case "/devices/state/changed":
                {
                    DiagnosticLogger.Log("GHUB Processing device state change");
                    if (ghubmsg.Payload == null)
                    {
                        DiagnosticLogger.LogWarning("Received device state change with null payload");
                        break;
                    }
                    ParseDeviceStateChange(ghubmsg.Payload);
                    break;
                }
            default:
                DiagnosticLogger.Log($"Unhandled LGHUB message path: {ghubmsg.Path}");
                break;
        }
    }

    protected void ParseDevicesList(JObject payload)
    {
        try
        {
            string payloadStr = payload.ToString(Formatting.None);
            string emptyPayloadStr = "{\"@type\":\"type.googleapis.com/logi.protocol.devices.Device.Info.List\"}";
            if (payload.ToString(Formatting.None) == emptyPayloadStr)
            {
                DiagnosticLogger.LogWarning("LoadDevices contains no useful fields - skipping.");
                return;
            }
            // Safe array extraction with type checking
            if (payload["deviceInfos"] is not JArray deviceInfos)
            {
                DiagnosticLogger.LogWarning("GHUB response missing or invalid 'deviceInfos' array");
                DiagnosticLogger.LogError("POSSIBLE PROTOCOL CHANGE - Please update LGSTrayBattery");
                DiagnosticLogger.LogError($"Received payload: {payload.ToString(Formatting.None)}");
                RecordProtocolError("LoadDevices - missing deviceInfos array");
                return;
            }

            int deviceCount = deviceInfos.Count;
            DiagnosticLogger.Log($"GHUB reported {deviceCount} device(s)");

            foreach (var deviceToken in deviceInfos)
            {
                // Validate that each device entry is a JObject
                if (deviceToken is not JObject deviceObj)
                {
                    DiagnosticLogger.LogWarning("GHUB device entry is not an JObject - skipping");
                    continue;
                }

                // Validate required fields BEFORE accessing them
                if (!GHubJsonHelpers.HasRequiredFields(deviceObj, "id", "extendedDisplayName"))
                {
                    DiagnosticLogger.LogError("Missing required fields in payload: id, extendedDisplayName  - PROTOCOL CHANGE DETECTED");
                    DiagnosticLogger.LogError($"Device data: {deviceObj.ToString(Formatting.None)}");
                    RecordProtocolError("LoadDevices - device missing required fields");
                    continue;
                }

                // Safe extraction using helper methods
                string deviceId = GHubJsonHelpers.GetStringOrDefault(deviceObj, "id", "unknown");
                string deviceName = GHubJsonHelpers.GetStringOrDefault(deviceObj, "extendedDisplayName", "Unknown Device");

                // Safe nested extraction for capabilities.hasBatteryStatus
                bool hasBattery = GHubJsonHelpers.GetNestedBoolOrDefault(
                    deviceObj,
                    ["capabilities", "hasBatteryStatus"],
                    false  // Default to false if field is missing
                );

                // Safe device type parsing
                string deviceTypeStr = GHubJsonHelpers.GetStringOrDefault(deviceObj, "deviceType", "mouse");
                if (!Enum.TryParse(deviceTypeStr, true, out DeviceType deviceType))
                {
                    DiagnosticLogger.LogWarning($"Unknown device type '{deviceTypeStr}', defaulting to Mouse");
                    deviceType = DeviceType.Mouse;
                }

                // Extract deviceSignature (stable identifier from GHUB)
                string deviceSignature = GetDeviceSignature(deviceObj);

                // Publish device with validated data
                _deviceEventBus.Publish(new InitMessage(
                    deviceId,
                    deviceName,
                    hasBattery,
                    deviceType,
                    deviceSignature
                ));

                DiagnosticLogger.Log($"GHub device registered - {deviceId} ({deviceName})");

                // Request initial battery state
                _ws?.Send(JsonConvert.SerializeObject(new
                {
                    msgId = "",
                    verb = "GET",
                    path = $"/battery/{deviceId}/state"
                }));
            }
        }
        catch (InvalidOperationException ex)
        {
            DiagnosticLogger.LogError($"GHUB protocol structure changed (InvalidOperation): {ex.Message}");
            DiagnosticLogger.LogError("PLEASE UPDATE LGSTrayBattery - GHUB API has changed");
            RecordProtocolError("LoadDevices - structure change");
        }
        catch (InvalidCastException ex)
        {
            DiagnosticLogger.LogError($"GHUB protocol type mismatch (InvalidCast): {ex.Message}");
            DiagnosticLogger.LogError("PLEASE UPDATE LGSTrayBattery - GHUB API has changed");
            RecordProtocolError("LoadDevices - type mismatch");
        }
        catch (Exception e)
        {
            DiagnosticLogger.LogError($"Unexpected error loading GHUB devices: {e.GetType().Name} - {e.Message}");
            DiagnosticLogger.LogError($"Stack trace: {e.StackTrace}");
        }
    }

    /// <summary>
    /// Retrieves a device signature used to uniquely identify GHUB device for state persistence.
    /// </summary>
    /// <param name="deviceObj">A JSON object containing device information.</param>
    /// <returns>deviceModel or extendedDisplayName</returns>
    private static string GetDeviceSignature(JObject deviceObj)
    {
        string deviceSignature;

        deviceSignature = GHubJsonHelpers.GetStringOrDefault(deviceObj, "deviceModel", "");
        if (!string.IsNullOrEmpty(deviceSignature))
        {
            return deviceSignature;
        }

        // extendedDisplayName is already verified to exist
        deviceSignature = GHubJsonHelpers.GetStringOrDefault(deviceObj, "extendedDisplayName", "");
        return deviceSignature;
    }

    /// <summary>
    /// Process single device info response (for reconnected devices)
    /// </summary>
    protected void LoadDevice(JObject? payload)
    {
        try
        {
            if (payload == null)
            {
                DiagnosticLogger.LogError("LoadDevice called with null payload");
                return;
            }

            // Validate required fields BEFORE accessing them
            if (!GHubJsonHelpers.HasRequiredFields(payload, "id", "extendedDisplayName"))
            {
                DiagnosticLogger.LogError("GHUB device missing required fields - PROTOCOL CHANGE DETECTED");
                DiagnosticLogger.LogError($"Device data: {payload.ToString(Formatting.None)}");
                RecordProtocolError("LoadDevice - missing required fields");
                return;
            }

            // Safe extraction using helper methods
            string deviceId = GHubJsonHelpers.GetStringOrDefault(payload, "id", "unknown");
            string deviceName = GHubJsonHelpers.GetStringOrDefault(payload, "extendedDisplayName", "Unknown Device");

            // Safe nested extraction for capabilities.hasBatteryStatus
            bool hasBattery = GHubJsonHelpers.GetNestedBoolOrDefault(
                payload,
                ["capabilities", "hasBatteryStatus"],
                false  // Default to false if field is missing
            );

            // Safe device type parsing
            string deviceTypeStr = GHubJsonHelpers.GetStringOrDefault(payload, "deviceType", "mouse");
            if (!Enum.TryParse(deviceTypeStr, true, out DeviceType deviceType))
            {
                DiagnosticLogger.LogWarning($"Unknown device type '{deviceTypeStr}', defaulting to Mouse");
                deviceType = DeviceType.Mouse;
            }

            // Extract deviceSignature (stable identifier from GHUB)
            string deviceSignature = GetDeviceSignature(payload);

            // Publish device with validated data
            _deviceEventBus.Publish(new InitMessage(
                deviceId,
                deviceName,
                hasBattery,
                deviceType,
                deviceSignature
            ));

            DiagnosticLogger.Log($"GHub device re-registered - {deviceId} ({deviceName})");

            // Request initial battery state
            _ws?.Send(JsonConvert.SerializeObject(new
            {
                msgId = "",
                verb = "GET",
                path = $"/battery/{deviceId}/state"
            }));
        }
        catch (InvalidOperationException ex)
        {
            string deviceId = payload?["id"]?.ToString() ?? "unknown";
            DiagnosticLogger.LogError($"GHUB protocol structure changed for device {deviceId} (InvalidOperation): {ex.Message}");
            DiagnosticLogger.LogError("PLEASE UPDATE LGSTrayBattery - GHUB API has changed");
            RecordProtocolError($"LoadDevice - structure change (device: {deviceId})");
        }
        catch (InvalidCastException ex)
        {
            string deviceId = payload?["id"]?.ToString() ?? "unknown";
            DiagnosticLogger.LogError($"GHUB protocol type mismatch for device {deviceId} (InvalidCast): {ex.Message}");
            DiagnosticLogger.LogError("PLEASE UPDATE LGSTrayBattery - GHUB API has changed");
            RecordProtocolError($"LoadDevice - type mismatch (device: {deviceId})");
        }
        catch (Exception e)
        {
            string deviceId = payload?["id"]?.ToString() ?? "unknown";
            DiagnosticLogger.LogError($"Unexpected error loading GHUB device {deviceId}: {e.GetType().Name} - {e.Message}");
            DiagnosticLogger.LogError($"Stack trace: {e.StackTrace}");
        }
    }

    protected void ParseBatteryUpdate(JObject payload)
    {
        string deviceId = "unknown";

        try
        {
            // Extract deviceId first for error reporting
            deviceId = GHubJsonHelpers.GetStringOrDefault(payload, "deviceId", "unknown");

            // Validate required fields
            if (!GHubJsonHelpers.HasRequiredFields(payload, "percentage", "charging"))
            {
                DiagnosticLogger.LogError($"GHUB battery update missing required fields for {deviceId}");
                DiagnosticLogger.LogError($"Payload: {payload.ToString(Formatting.None)}");
                DiagnosticLogger.LogError("POSSIBLE PROTOCOL CHANGE - Please update LGSTrayBattery");
                RecordProtocolError($"ParseBatteryUpdate - missing required fields (device: {deviceId})");
                return;
            }

            // Safe extraction with type checking
            int? percentage = GHubJsonHelpers.GetInt(payload, "percentage");
            bool? charging = GHubJsonHelpers.GetBool(payload, "charging");
            double? mileage = GHubJsonHelpers.GetDouble(payload, "mileage");

            // Validate extracted values
            if (!percentage.HasValue || !charging.HasValue)
            {
                DiagnosticLogger.LogError($"GHUB battery fields have wrong types for {deviceId}");
                DiagnosticLogger.LogError($"Payload: {payload.ToString(Formatting.None)}");
                DiagnosticLogger.LogError("POSSIBLE PROTOCOL CHANGE - Please update LGSTrayBattery");
                RecordProtocolError($"ParseBatteryUpdate - wrong field types (device: {deviceId})");
                return;
            }

            // Validate percentage range
            if (percentage.Value < 0 || percentage.Value > 100)
            {
                DiagnosticLogger.LogWarning($"GHUB reported invalid battery percentage {percentage.Value}% for {deviceId}");
                percentage = Math.Clamp(percentage.Value, 0, 100);
            }

            _deviceEventBus.Publish(new UpdateMessage(
                deviceId,
                percentage.Value,
                charging.Value
                    ? PowerSupplyStatus.CHARGING
                    : PowerSupplyStatus.NOT_CHARGING,
                0,  // No voltage from GHUB
                DateTime.Now,
                mileage ?? 0.0  // Default to 0 if missing
            ));
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"GHUB Failed to parse battery update for device {deviceId}: {ex.GetType().Name} - {ex.Message}");
            DiagnosticLogger.LogError("POSSIBLE PROTOCOL CHANGE - Please update LGSTrayBattery");
        }
    }

    /// <summary>
    /// Handle /devices/state/changed events (device connect/disconnect)
    /// </summary>
    protected void ParseDeviceStateChange(JObject payload)
    {
        try
        {
            string deviceId = payload["id"]?.ToString() ?? "unknown";
            string state = payload["state"]?.ToString()?.ToLower() ?? "";

            DiagnosticLogger.Log($"GHUB device state change - {deviceId}: {state}");
            DiagnosticLogger.Log($"Full state change payload: {payload.ToString(Formatting.None)}");

            switch (state)
            {
                case "not_connected":
                    // Device disconnected - publish removal
                    _deviceEventBus.Publish(new RemoveMessage(deviceId, "ghub_disconnect"));
                    DiagnosticLogger.Log($"Device removed via GHUB disconnect - {deviceId}");
                    break;

                case "active":
                    // Device reconnected - check if payload contains device info
                    if (payload["deviceType"] != null && payload["extendedDisplayName"] != null)
                    {
                        // Payload contains full device info, use it directly
                        DiagnosticLogger.Log($"Device reconnected with full info in payload - re-registering: {deviceId}");
                        LoadDevice(payload);
                    }
                    else
                    {
                        // Payload doesn't contain device info, request full device list
                        DiagnosticLogger.Log($"Device reconnected without full info - requesting device list");
                        LoadDevices();
                    }
                    break;

                default:
                    DiagnosticLogger.Log($"Unknown device state: {state}");
                    break;
            }
        }
        catch (Exception ex)
        {
            string deviceId = payload?["id"]?.ToString() ?? "unknown";
            DiagnosticLogger.LogError($"Failed to parse device state change for {deviceId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Record a protocol error and notify user if threshold is exceeded.
    /// </summary>
    /// <param name="context">Description of where the error occurred</param>
    private void RecordProtocolError(string context)
    {
        _protocolErrorCount++;

        if (_protocolErrorCount >= PROTOCOL_ERROR_THRESHOLD)
        {
            // Only notify once per hour to avoid spam
            if ((DateTime.Now - _lastProtocolErrorNotification).TotalHours >= 1)
            {
                DiagnosticLogger.LogError("============================================================");
                DiagnosticLogger.LogError("GHUB PROTOCOL ERRORS DETECTED");
                DiagnosticLogger.LogError($"Multiple protocol errors in: {context}");
                DiagnosticLogger.LogError("Logitech may have changed their GHUB WebSocket API");
                DiagnosticLogger.LogError("Please check for LGSTrayBattery updates at:");
                DiagnosticLogger.LogError("https://github.com/your-repo/LGSTrayBattery/releases");
                DiagnosticLogger.LogError("============================================================");

                _lastProtocolErrorNotification = DateTime.Now;
            }
        }
    }

    public async Task RediscoverDevices()
    {
        // First, remove all GHUB devices to prevent duplicates
        _deviceEventBus.Publish(new RemoveMessage("*GHUB*", "rediscover_cleanup"));
        DiagnosticLogger.Log("Clearing all GHUB devices before rediscovery");

        // Wait 100ms for removal to propagate through MessagePipe
        await Task.Delay(100);

        // Now reconnect and discover devices fresh
        using var cts = new CancellationTokenSource();
        await StopAsync(cts.Token);
        await StartAsync(cts.Token);

        DiagnosticLogger.Log("GHUB device rediscovery complete");
    }
}
