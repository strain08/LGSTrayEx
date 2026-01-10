using CommunityToolkit.Mvvm.Messaging;
using LGSTrayHID.Battery;
using LGSTrayPrimitives;
using LGSTrayPrimitives.Interfaces;
using LGSTrayPrimitives.Retry;
using LGSTrayUI.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MQTTnet;
using Notification.Wpf;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LGSTrayUI.Services;

internal class MQTTService : IHostedService, IRecipient<DeviceBatteryUpdatedMessage>, IDisposable
{
    // Dependencies
    private readonly MQTTSettings _mqttSettings;
    private readonly NotificationSettings _notificationSettings;
    private readonly INotificationManager? _notificationManager;
    private readonly IMessenger _messenger;
    private readonly EventThrottler _eventThrottler = new(TimeSpan.FromSeconds(30)); // 30s throttle for connection error notifications

    // MQTT client
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientFactory _clientFactory;
    private readonly BackoffStrategy _reconnectBackoff;

    // State tracking
    private readonly Dictionary<string, DateTimeOffset> _lastPublishTimes = [];
    private readonly HashSet<string> _publishedDevices = [];
    private readonly object _publishLock = new();

    // Connection state
    private CancellationTokenSource? _reconnectCts;
    private bool _disposedValue;

    public MQTTService(IOptions<AppSettings> appSettings,
                       INotificationManager? notificationManager,
                       IMessenger messenger)
    {
        _mqttSettings = appSettings.Value.MQTT;

#if DEBUG
        _mqttSettings.BrokerAddress = "192.168.20.100";
#endif

        _notificationSettings = appSettings.Value.Notifications;
        _notificationManager = notificationManager;
        _messenger = messenger;

        _clientFactory = new MqttClientFactory();
        _mqttClient = _clientFactory.CreateMqttClient();

        // Configure reconnect backoff strategy
        _reconnectBackoff = new BackoffStrategy(
            initialDelay: TimeSpan.FromSeconds(2),
            maxDelay: TimeSpan.FromSeconds(60),
            initialTimeout: TimeSpan.FromSeconds(5),
            maxTimeout: TimeSpan.FromSeconds(30),
            multiplier: 2.0
        )
        { ProfileName = "MQTTReconnect" };

        // Subscribe to MQTT client events
        _mqttClient.ConnectedAsync += OnConnectedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
    }

    // IHostedService Implementation
    public Task StartAsync(CancellationToken cancellationToken)
    {
        DiagnosticLogger.Log("[MQTT] Service starting...");

        // Subscribe to device battery updates
        _messenger.Register<DeviceBatteryUpdatedMessage>(this);

        // Start connection (fire-and-forget like GHubManager)
        _ = ConnectAsync();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DiagnosticLogger.Log("[MQTT] Service stopping...");

        // Unregister from messenger
        _messenger.UnregisterAll(this);

        // Cancel reconnection attempts
        _reconnectCts?.Cancel();

        // Disconnect gracefully
        if (_mqttClient.IsConnected)
        {
            try
            {
                // Publish offline status gracefully before disconnecting
                PublishServiceAvailabilityAsync(false).Wait(TimeSpan.FromSeconds(2), cancellationToken);

                _mqttClient.DisconnectAsync(cancellationToken: cancellationToken).Wait(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogWarning($"[MQTT] Error during disconnect: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    // Connection Management
    private async Task ConnectAsync()
    {
        _reconnectCts = new CancellationTokenSource();
        int attemptNumber = 0;
        bool notificationShown = false;

        await foreach (var attempt in _reconnectBackoff.GetAttemptsAsync(_reconnectCts.Token))
        {
            attemptNumber++;

            // Apply delay before retry
            if (attempt.AttemptNumber > 1)
            {
                DiagnosticLogger.Log($"[MQTT] Reconnecting in {attempt.Delay.TotalSeconds:F1}s (attempt {attempt.AttemptNumber})...");
                await Task.Delay(attempt.Delay, _reconnectCts.Token);
            }

            try
            {
                DiagnosticLogger.Log($"[MQTT] Connecting to broker {_mqttSettings.BrokerAddress}:{_mqttSettings.Port}...");

                var clientId = string.IsNullOrEmpty(_mqttSettings.ClientId)
                    ? $"LGSTrayBattery_{Environment.MachineName}"
                    : _mqttSettings.ClientId;

                var optionsBuilder = new MqttClientOptionsBuilder()
                        .WithTcpServer(_mqttSettings.BrokerAddress, _mqttSettings.Port)
                        .WithClientId(clientId)
                        .WithCleanSession(false); // Preserve session for retained messages

                // Add credentials if configured
                if (!string.IsNullOrEmpty(_mqttSettings.Username))
                {
                    optionsBuilder.WithCredentials(_mqttSettings.Username, _mqttSettings.Password);
                }

                // Add TLS if enabled
                if (_mqttSettings.UseTLS)
                {
                    optionsBuilder.WithTlsOptions(o => o.UseTls());
                }

                // Add Last Will and Testament (LWT)
                // This ensures HA marks devices as unavailable if the app crashes
                if (_mqttSettings.PublishLWT) 
                {
                    var statusTopic = $"{_mqttSettings.TopicPrefix}/status";
                    optionsBuilder.WithWillTopic(statusTopic)
                                  .WithWillPayload("offline")
                                  .WithWillRetain(true)
                                  .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
                }



                var options = optionsBuilder.Build();

                // Connect with timeout
                using var timeoutCts = new CancellationTokenSource(attempt.Timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _reconnectCts.Token, timeoutCts.Token);

                await _mqttClient.ConnectAsync(options, linkedCts.Token);

                // Success - break retry loop
                DiagnosticLogger.Log("[MQTT] Connected successfully");
                break;
            }
            catch (OperationCanceledException) when (_reconnectCts.Token.IsCancellationRequested)
            {
                DiagnosticLogger.Log("[MQTT] Connection cancelled (service stopping)");
                return;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogWarning($"[MQTT] Connection attempt {attemptNumber} failed: {ex.Message}");

                // Show notification on first failure only (avoid spam)
                if (!notificationShown)
                {
                    ShowConnectionNotification(false, ex.Message);
                    notificationShown = true;
                }
            }
        }
    }

    // Event Handlers
    private Task OnConnectedAsync(MqttClientConnectedEventArgs e)
    {
        DiagnosticLogger.Log("[MQTT] Connection established");
        ShowConnectionNotification(true, null);

        // Publish Service Availability (Birth Message)
        _ = PublishServiceAvailabilityAsync(true);

        // Clear published devices tracking to re-publish discovery configs
        lock (_publishLock)
        {
            _publishedDevices.Clear();
        }

        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        DiagnosticLogger.LogWarning($"[MQTT] Disconnected: {e.Reason}");

        // Only show notification for unexpected disconnects (not during shutdown)
        if (e.Reason != MqttClientDisconnectReason.NormalDisconnection
            && _reconnectCts != null && !_reconnectCts.Token.IsCancellationRequested)
        {
            ShowConnectionNotification(false, e.ReasonString);

            // Auto-reconnect
            _ = ConnectAsync();
        }

        return Task.CompletedTask;
    }

    // Message Handler
    public void Receive(DeviceBatteryUpdatedMessage message)
    {
        var device = message.Device;

        // Filter: Only report Native devices (ignore GHub/Other sources)
        if (device.DataSource != DataSource.Native)
        {
            return;
        }

        // Check connection
        if (!_mqttClient.IsConnected)
        {
            return;
        }

        // Handle offline devices
        if (!device.IsOnline)
        {
            DiagnosticLogger.Log($"[MQTT] Device went offline: {device.DeviceName}");

            // Always publish state update with online=false so HA knows device is offline
            _ = PublishStateUpdateAsync(device);

            // Optionally publish availability topic (marks sensor as unavailable in HA)
            if (_mqttSettings.PublishLWT)
            {
                _ = PublishAvailabilityAsync(device.DeviceId, false);
            }
            return;
        }

        // Check throttle for online devices
        if (!ShouldPublish(device.DeviceId)) {
            return;
        }

        // Publish discovery config (idempotent - safe to call multiple times)
        _ = PublishDiscoveryConfigAsync(device);

        // Publish state update
        _ = PublishStateUpdateAsync(device);
    }

    // Throttling
    private bool ShouldPublish(string deviceId)
    {
        if (_mqttSettings.PublishThrottleSeconds == 0) {
            return true;
        }

        lock (_publishLock)
        {
            var now = DateTimeOffset.Now;

            if (_lastPublishTimes.TryGetValue(deviceId, out var lastPublish))
            {
                var elapsed = now - lastPublish;
                if (elapsed.TotalSeconds < _mqttSettings.PublishThrottleSeconds)
                {
                    return false;
                }
            }

            _lastPublishTimes[deviceId] = now;
            return true;
        }
    }

    // Discovery Publishing
    private async Task PublishDiscoveryConfigAsync(LogiDeviceViewModel device)
    {
        try
        {
            // Skip if already published
            lock (_publishLock)
            {
                if (_publishedDevices.Contains(device.DeviceId))
                {
                    return;
                }
            }

            var deviceId = SanitizeForMqtt(device.DeviceId);
            var _sw_version = NotifyIconViewModel.AssemblyVersion;

            // Shared device registry info
            var deviceRegistry = new
            {
                identifiers = new[] { $"lgstray_{deviceId}" },
                name = device.DeviceName,
                model = device.DeviceType.ToString(),
                manufacturer = "Logitech",
                sw_version = $"LGSTrayBattery {_sw_version}"
            };

            // Shared availability config (Device AND Service must be online)
            var availabilityConfig = new[]
            {
                // Global Service Status (Bridge)
                new
                {
                    topic = $"{_mqttSettings.TopicPrefix}/status",
                    payload_available = "online",
                    payload_not_available = "offline"
                },
                // Device Specific Status
                new
                {
                    topic = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/availability",
                    payload_available = "online",
                    payload_not_available = "offline"
                }
            };

            // 1. Battery Level Sensor Config
            var batteryConfig = new
            {
                name = $"{device.DeviceName} Battery",
                unique_id = $"lgstray_{deviceId}_battery",
                state_topic = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state",
                device_class = "battery",
                state_class = "measurement", // Enable long-term stats
                unit_of_measurement = "%",
                value_template = "{{ value_json.percentage }}",
                json_attributes_topic = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state",
                device = deviceRegistry,
                icon = GetDeviceIcon(device.DeviceType),
                availability = availabilityConfig,
                availability_mode = "all" // Default, but explicit: BOTH must be online
            };

            // 2. Charging Status Binary Sensor Config
            var chargingConfig = new
            {
                name = $"{device.DeviceName} Charging",
                unique_id = $"lgstray_{deviceId}_charging",
                state_topic = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state",
                device_class = "battery_charging",
                value_template = "{{ 'ON' if value_json.charging else 'OFF' }}",
                device = deviceRegistry,
                availability = availabilityConfig,
                availability_mode = "all"
            };

            // 3. Connectivity Binary Sensor Config (Useful when PublishOfflineStatus = false)
            var connectivityConfig = new
            {
                name = $"{device.DeviceName} Status",
                unique_id = $"lgstray_{deviceId}_connectivity",
                state_topic = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state",
                device_class = "connectivity",
                value_template = "{{ 'ON' if value_json.online else 'OFF' }}",
                device = deviceRegistry,
                availability = availabilityConfig,
                availability_mode = "all"
            };

            // Publish Battery Sensor Config
            var batteryPayload = System.Text.Json.JsonSerializer.Serialize(batteryConfig);
            var batteryTopic = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/config";
            
            var batteryMessage = new MqttApplicationMessageBuilder()
                .WithTopic(batteryTopic)
                .WithPayload(batteryPayload)
                .WithRetainFlag(_mqttSettings.RetainMessages)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(batteryMessage, CancellationToken.None);

            // Publish Charging Binary Sensor Config
            var chargingPayload = System.Text.Json.JsonSerializer.Serialize(chargingConfig);
            var chargingTopic = $"{_mqttSettings.TopicPrefix}/binary_sensor/{deviceId}/charging/config";

            var chargingMessage = new MqttApplicationMessageBuilder()
                .WithTopic(chargingTopic)
                .WithPayload(chargingPayload)
                .WithRetainFlag(_mqttSettings.RetainMessages)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(chargingMessage, CancellationToken.None);

            // Publish Connectivity Binary Sensor Config
            var connectivityPayload = System.Text.Json.JsonSerializer.Serialize(connectivityConfig);
            var connectivityTopic = $"{_mqttSettings.TopicPrefix}/binary_sensor/{deviceId}/connectivity/config";

            var connectivityMessage = new MqttApplicationMessageBuilder()
                .WithTopic(connectivityTopic)
                .WithPayload(connectivityPayload)
                .WithRetainFlag(_mqttSettings.RetainMessages)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(connectivityMessage, CancellationToken.None);

            lock (_publishLock)
            {
                _publishedDevices.Add(device.DeviceId);
            }

            DiagnosticLogger.Log($"[MQTT] Published discovery config for {device.DeviceName}");

            // Publish initial availability
            await PublishAvailabilityAsync(device.DeviceId, device.IsOnline);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[MQTT] Failed to publish discovery config for {device.DeviceName}: {ex.Message}");
        }
    }

    // State Publishing
    private async Task PublishStateUpdateAsync(LogiDeviceViewModel device)
    {
        try
        {
            var deviceId = SanitizeForMqtt(device.DeviceId);
            var topic = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state";

            var state = new {
                percentage = (int)Math.Round(device.BatteryPercentage),
                voltage = device.BatteryVoltage,
                charging = device.PowerSupplyStatus == PowerSupplyStatus.CHARGING,
                online = device.IsOnline,
                is_wired = device.IsWiredMode,
                power_status = device.PowerSupplyStatus.ToString(),
                signature = device.DeviceSignature,
                last_update = device.LastUpdate.ToString("o"),
                device_name = device.DeviceName,
                device_type = device.DeviceType.ToString(),
                machine_name = Environment.MachineName
            };

            var payload = System.Text.Json.JsonSerializer.Serialize(state);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(_mqttSettings.RetainMessages)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, CancellationToken.None);

            DiagnosticLogger.Log($"[MQTT] Published state for {device.DeviceName}: {state.percentage}% (online: {device.IsOnline})");

            // Only update availability to "online" for online devices
            // Offline availability is managed by the message handler based on publishOfflineStatus setting
            if (device.IsOnline)
            {
                await PublishAvailabilityAsync(device.DeviceId, true);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[MQTT] Failed to publish state for {device.DeviceName}: {ex.Message}");
        }
    }

    private async Task PublishServiceAvailabilityAsync(bool isOnline)
    {
        try
        {
            var topic = $"{_mqttSettings.TopicPrefix}/status";
            var payload = isOnline ? "online" : "offline";

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(true) // Always retain service status
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"[MQTT] Failed to publish service availability: {ex.Message}");
        }
    }

    private async Task PublishAvailabilityAsync(string deviceId, bool isOnline)
    {
        try
        {
            var sanitizedId = SanitizeForMqtt(deviceId);
            var topic = $"{_mqttSettings.TopicPrefix}/sensor/{sanitizedId}/battery/availability";
            var payload = isOnline ? "online" : "offline";

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(_mqttSettings.RetainMessages)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"[MQTT] Failed to publish availability: {ex.Message}");
        }
    }

    // Helpers
    private static string SanitizeForMqtt(string input)
    {
        // Replace invalid MQTT topic characters
        return input.Replace(" ", "_")
                    .Replace("/", "_")
                    .Replace("+", "_")
                    .Replace("#", "_");
    }

    private static string GetDeviceIcon(DeviceType deviceType)
    {
        return deviceType switch
        {
            DeviceType.Mouse => "mdi:mouse",
            DeviceType.Keyboard => "mdi:keyboard",
            DeviceType.Headset => "mdi:headset",
            _ => "mdi:battery"
        };
    }

    private void ShowConnectionNotification(bool connected, string? errorMessage)
    {
        // Check if notifications are enabled
        if (!_mqttSettings.NotifyConnectionStatus || !_notificationSettings.Enabled)
        {
            return;
        }

        // Check if notification manager is available
        if (_notificationManager == null)
        {
            return;
        }

        try
        {
            if (connected)
            {
                _notificationManager.Show(
                    "MQTT - Connected",
                    $"Successfully connected to {_mqttSettings.BrokerAddress}:{_mqttSettings.Port}",
                    NotificationType.Success,
                    areaName: "",
                    expirationTime: TimeSpan.FromSeconds(5)
                );
                _eventThrottler.Reset();
            }
            else
            {
                // Do not spam user with connection fail notifications
                if (!_eventThrottler.ShouldProcessEvent(DateTimeOffset.Now)) 
                    return;

                var message = string.IsNullOrEmpty(errorMessage)
                    ? $"Failed to connect to {_mqttSettings.BrokerAddress}:{_mqttSettings.Port}"
                    : $"Connection error: {errorMessage}";

                _notificationManager.Show(
                    "MQTT - Connection Failed",
                    message,
                    NotificationType.Warning,
                    areaName: "",
                    expirationTime: TimeSpan.FromSeconds(10)
                );
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"[MQTT] Failed to show notification: {ex.Message}");
        }
    }

    // IDisposable
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _reconnectCts?.Cancel();
                _reconnectCts?.Dispose();

                _mqttClient?.Dispose();
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
