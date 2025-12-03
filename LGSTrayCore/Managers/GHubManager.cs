using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Swan;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Websocket.Client;

namespace LGSTrayCore.Managers
{
    file struct GHUBMsg
    {
        public string MsgId { get; set; }
        public string Verb { get; set; }
        public string Path { get; set; }
        public string Origin { get; set; }
        public JObject Result { get; set; }
        public JObject Payload { get; set; }

        public static GHUBMsg DeserializeJson(string json)
        {
            return JsonConvert.DeserializeObject<GHUBMsg>(json);
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
                    _ws?.Dispose();
                    _ws = null;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
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

        private const string WEBSOCKET_SERVER = "ws://localhost:9010";

        [GeneratedRegex(@"\/battery\/dev[0-9a-zA-Z]+\/state")]
        private static partial Regex BatteryDeviceStateRegex();

        private readonly IPublisher<IPCMessage> _deviceEventBus;

        protected WebsocketClient? _ws;

        public GHubManager(IPublisher<IPCMessage> deviceEventBus)
        {
            _deviceEventBus = deviceEventBus;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var url = new Uri(WEBSOCKET_SERVER);

            var factory = new Func<ClientWebSocket>(() =>
            {
                var client = new ClientWebSocket();
                client.Options.UseDefaultCredentials = false;
                client.Options.SetRequestHeader("Origin", "file://");
                client.Options.SetRequestHeader("Pragma", "no-cache");
                client.Options.SetRequestHeader("Cache-Control", "no-cache");
                client.Options.SetRequestHeader("Sec-WebSocket-Extensions", "permessage-deflate; client_max_window_bits");
                client.Options.SetRequestHeader("Sec-WebSocket-Protocol", "json");
                client.Options.AddSubProtocol("json");
                return client;
            });

            _ws = new WebsocketClient(url, factory);
            _ws.MessageReceived.Subscribe(ParseSocketMsg);
            _ws.ErrorReconnectTimeout = TimeSpan.FromMilliseconds(500);
            _ws.ReconnectTimeout = null;

            DiagnosticLogger.Log($"Attempting to connect to LGHUB at {url}");

            try
            {
                await _ws.Start();
            }
            catch (Websocket.Client.Exceptions.WebsocketException ex)
            {
                DiagnosticLogger.LogError($"Failed to connect to LGHUB: {ex.Message}");
                this.Dispose();
                return;
            }

            DiagnosticLogger.Log("Connected to LGHUB successfully");

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

            DiagnosticLogger.Log("Requesting device list from LGHUB");
            LoadDevices();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _ws?.Dispose();

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
            GHUBMsg ghubmsg = GHUBMsg.DeserializeJson(msg.Text!);
            DiagnosticLogger.Log($"LGHUB message received - Path: {ghubmsg.Path}");

            switch (ghubmsg.Path)
            {
                case "/devices/list":
                    {
                        DiagnosticLogger.Log("Processing /devices/list response");
                        LoadDevices(ghubmsg.Payload);
                        break;
                    }
                case "/battery/state/changed":
                case { } when BatteryDeviceStateRegex().Match(ghubmsg.Path).Success:
                    {
                        DiagnosticLogger.Log($"Processing battery update: {ghubmsg.Path}");
                        ParseBatteryUpdate(ghubmsg.Payload);
                        break;
                    }
                default:
                    DiagnosticLogger.Log($"Unhandled LGHUB message path: {ghubmsg.Path}");
                    break;
            }
        }

        protected void LoadDevices(JObject payload)
        {
            try
            {
                var deviceInfos = payload["deviceInfos"];
                if (deviceInfos == null)
                {
                    DiagnosticLogger.LogWarning("LGHUB response missing 'deviceInfos' field");
                    return;
                }

                int deviceCount = deviceInfos.Count();
                DiagnosticLogger.Log($"LGHUB reported {deviceCount} device(s)");

                foreach (var deviceToken in deviceInfos)
                {
                    if (!Enum.TryParse(deviceToken["deviceType"]!.ToString(), true, out DeviceType deviceType))
                    {
                        deviceType = DeviceType.Mouse;
                    }

                    string deviceId = deviceToken["id"]!.ToString();
                    string deviceName = deviceToken["extendedDisplayName"]!.ToString();
                    _deviceEventBus.Publish(new InitMessage(
                        deviceId,
                        deviceName,
                        (bool) deviceToken["capabilities"]!["hasBatteryStatus"]!,
                        deviceType
                    ));

                    DiagnosticLogger.Log($"GHub device registered - {deviceId} ({deviceName})");

                    _ws?.Send(JsonConvert.SerializeObject(new
                    {
                        msgId = "",
                        verb = "GET",
                        path = $"/battery/{deviceId}/state"
                    }));
                }
            }
            catch (Exception e)
            {
                if (e is NullReferenceException || e is JsonReaderException)
                {
                    DiagnosticLogger.LogError($"Failed to parse LGHUB device list: {e.Message}");
                }
                else
                {
                    DiagnosticLogger.LogError($"Unexpected error loading LGHUB devices: {e.Message}");
                }
            }
        }

        protected void ParseBatteryUpdate(JObject payload)
        {
            try
            {
                string deviceId = payload["deviceId"]?.ToString() ?? "unknown";
                _deviceEventBus.Publish(new UpdateMessage(
                    deviceId,
                    payload["percentage"]!.ToObject<int>(),
                    payload["charging"]!.ToBoolean() ? PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING : PowerSupplyStatus.POWER_SUPPLY_STATUS_NOT_CHARGING,
                    0,
                    DateTime.Now,
                    payload["mileage"]!.ToObject<double>()
                ));
            }
            catch (Exception ex)
            {
                string deviceId = payload?["deviceId"]?.ToString() ?? "unknown";
                DiagnosticLogger.LogError($"Failed to parse battery update for device {deviceId}: {ex.Message}");
            }
        }

        public async void RediscoverDevices()
        {
            using var cts = new CancellationTokenSource();
            await StopAsync(cts.Token);
            await StartAsync(cts.Token);
        }
    }
}
