using LGSTrayCore;
using LGSTrayCore.Interfaces;
using LGSTrayHID.Battery;
using LGSTrayPrimitives;
using LGSTrayPrimitives.Interfaces;
using LGSTrayPrimitives.Retry;
using LGSTrayUI.Messages;
using MessagePipe;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MQTTnet;
using Notification.Wpf;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LGSTrayUI.Services;

internal class MQTTService : IHostedService, IDisposable
{
    // Dependencies
    private readonly MQTTSettings _mqttSettings;
    private readonly NotificationSettings _notificationSettings;
    private readonly INotificationManager? _notificationManager;
    private readonly EventThrottler _eventThrottler = new(TimeSpan.FromSeconds(30)); // 30s throttle for connection error notifications

    private readonly ISubscriber<DeviceBatteryUpdatedMessage> _batterySubscriber;
    private readonly ISubscriber<SystemSuspendingMessage> _suspendSubscriber;
    private readonly ISubscriber<ForceRepublishMessage> _republishSubscriber;
    private readonly IPublisher<ForceRepublishMessage> _republishPublisher;
    private IDisposable? _batterySubscription;
    private IDisposable? _suspendSubscription;
    private IDisposable? _republishSubscription;

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
    private bool _isConnecting = false;
    private readonly object _connectionLock = new();

    /// <summary>
    /// Safely checks if the MQTT client is connected and not disposed.
    /// </summary>
    private bool IsClientConnected
    {
        get
        {
            if (_disposedValue) return false;
            try
            {
                return _mqttClient.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public MQTTService(IOptions<AppSettings> appSettings,
                       INotificationManager? notificationManager,
                       ISubscriber<DeviceBatteryUpdatedMessage> batterySubscriber,
                       ISubscriber<SystemSuspendingMessage> suspendSubscriber,
                       ISubscriber<ForceRepublishMessage> republishSubscriber,
                       IPublisher<ForceRepublishMessage> republishPublisher)
    {
        _mqttSettings = appSettings.Value.MQTT;
        _notificationSettings = appSettings.Value.Notifications;
        _notificationManager = notificationManager;
        _batterySubscriber = batterySubscriber;
        _suspendSubscriber = suspendSubscriber;
        _republishSubscriber = republishSubscriber;
        _republishPublisher = republishPublisher;

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
        _mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceivedAsync;
    }

    // IHostedService Implementation
    public Task StartAsync(CancellationToken cancellationToken)
    {
        DiagnosticLogger.Log("[MQTT] Service starting...");

        // Subscribe to device battery updates
        _batterySubscription = _batterySubscriber.Subscribe(OnBatteryUpdate);

        // Subscribe to system suspend events
        _suspendSubscription = _suspendSubscriber.Subscribe(OnSystemSuspending);

        // Subscribe to force republish messages (for HA birth)
        _republishSubscription = _republishSubscriber.Subscribe(OnForceRepublish);

        // Start connection (fire-and-forget like GHubManager)
        _ = ConnectAsync();

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        DiagnosticLogger.Log("[MQTT] Service stopping...");

        // Check if already disposed
        if (_disposedValue)
        {
            DiagnosticLogger.Log("[MQTT] Service already disposed, skipping StopAsync");
            return;
        }

        // Unregister from messenger first to stop receiving new updates
        _batterySubscription?.Dispose();
        _suspendSubscription?.Dispose();
        _republishSubscription?.Dispose();

        // Cancel reconnection attempts BEFORE any disconnect operations
        // This prevents OnDisconnectedAsync from triggering reconnection
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;

        // Disconnect gracefully - check client is still valid
        var isConnected = false;
        try
        {
            isConnected = _mqttClient.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            DiagnosticLogger.Log("[MQTT] Client already disposed");
            return;
        }

        DiagnosticLogger.Log($"[MQTT] Client connected: {isConnected}");

        if (isConnected)
        {
            try
            {
                // Unsubscribe from MQTT client events BEFORE disconnecting
                // This prevents OnDisconnectedAsync from being called during shutdown
                DiagnosticLogger.Log("[MQTT] Unsubscribing from MQTT client events...");
                _mqttClient.ConnectedAsync -= OnConnectedAsync;
                _mqttClient.DisconnectedAsync -= OnDisconnectedAsync;
                _mqttClient.ApplicationMessageReceivedAsync -= OnMqttMessageReceivedAsync;

                DiagnosticLogger.Log("[MQTT] Disconnecting from broker...");
                // Use explicit timeout for disconnect - don't rely on cancellation token alone
                // LWT will automatically mark devices as unavailable
                using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnectCts.Token);
                await _mqttClient.DisconnectAsync(cancellationToken: linkedCts.Token);
                DiagnosticLogger.Log("[MQTT] Disconnected from broker");
            }
            catch (OperationCanceledException)
            {
                DiagnosticLogger.LogWarning("[MQTT] Disconnect operation timed out or was cancelled");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError($"[MQTT] Error during disconnect: {ex.Message}");
            }
        }
        else
        {
            DiagnosticLogger.Log("[MQTT] Client not connected, skipping graceful shutdown");
        }
    }

    // Connection Management
    private async Task ConnectAsync()
    {
        // Prevent multiple concurrent connection attempts
        lock (_connectionLock)
        {
            if (_isConnecting || _mqttClient.IsConnected)
            {
                DiagnosticLogger.Log("[MQTT] Connection attempt skipped (already connecting or connected)");
                return;
            }
            _isConnecting = true;
        }

        try
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
                // Use host-specific status topic so multiple hosts don't override each other
                var statusTopic = GetHostStatusTopic();
                optionsBuilder.WithWillTopic(statusTopic)
                              .WithWillPayload("offline")
                              .WithWillRetain(true)
                              .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

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
        finally
        {
            lock (_connectionLock)
            {
                _isConnecting = false;
            }
        }
    }

    // Event Handlers
    private async Task OnConnectedAsync(MqttClientConnectedEventArgs e)
    {
        DiagnosticLogger.Log("[MQTT] Connection established");
        ShowConnectionNotification(true, null);

        // Subscribe to this host's status topic (for detecting reconnection/HA restart)
        var birthTopic = GetHostStatusTopic();
        try
        {
            await _mqttClient.SubscribeAsync(birthTopic);
            DiagnosticLogger.Log($"[MQTT] Subscribed to host status topic: {birthTopic}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"[MQTT] Failed to subscribe to HA birth topic: {ex.Message}");
        }

        // Publish Service Availability (Birth Message)
        _ = PublishServiceAvailabilityAsync(true);

        // Publish Host Device Discovery Config (if LWT enabled)
        _ = PublishHostDiscoveryConfigAsync();

        // Clear published devices tracking to re-publish discovery configs
        lock (_publishLock)
        {
            _publishedDevices.Clear();
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        DiagnosticLogger.LogWarning($"[MQTT] Disconnected: {e.Reason}");

        // Only show notification for unexpected disconnects (not during normal shutdown)
        if (e.Reason != MqttClientDisconnectReason.NormalDisconnection
            && _reconnectCts != null && !_reconnectCts.Token.IsCancellationRequested)
        {
            ShowConnectionNotification(false, e.ReasonString);
        }

        // Auto-reconnect for all disconnections (unless we're shutting down LGSTray)
        if (_reconnectCts != null && !_reconnectCts.Token.IsCancellationRequested)
        {
            DiagnosticLogger.Log("[MQTT] Starting reconnection attempt...");
            _ = ConnectAsync();
        }

        return Task.CompletedTask;
    }

    // Message Handlers
    private Task OnMqttMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payloadBytes = e.ApplicationMessage.Payload.ToArray();
        var payload = System.Text.Encoding.UTF8.GetString(payloadBytes);

        // Check if this is our host's status topic (for detecting reconnection)
        var birthTopic = GetHostStatusTopic();
        if (topic == birthTopic && payload == "online")
        {
            DiagnosticLogger.Log("[MQTT] Home Assistant came online - triggering device republish");

            // Clear published devices cache to force re-discovery
            lock (_publishLock)
            {
                _publishedDevices.Clear();
            }

            // Trigger republish of all devices by sending message to LogiDeviceCollection
            _republishPublisher.Publish(new ForceRepublishMessage());
        }

        return Task.CompletedTask;
    }

    private void OnForceRepublish(ForceRepublishMessage message)
    {
        DiagnosticLogger.Log("[MQTT] Force republish requested - clearing discovery cache");

        // Clear published devices cache to trigger re-discovery
        lock (_publishLock)
        {
            _publishedDevices.Clear();
        }
    }

    private void OnSystemSuspending(SystemSuspendingMessage message)
    {
        DiagnosticLogger.Log("[MQTT] System suspending");
        // LWT will handle marking devices as unavailable if the connection drops during sleep
        // No explicit action needed here
    }

    public void OnBatteryUpdate(DeviceBatteryUpdatedMessage message)
    {
        var device = message.Device;

        // Filter: Only report Native devices (ignore GHub/Other sources)
        if (device.DataSource != DataSource.Native)
        {
            return;
        }

        // Check connection (safe check handles disposed state)
        if (!IsClientConnected)
        {
            return;
        }

        // Skip offline device updates - we don't track device online/offline status anymore
        // LWT handles service-level availability when LGSTray disconnects
        if (!device.IsOnline)
        {
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
    private async Task PublishDiscoveryConfigAsync(LogiDevice device)
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
            var machineName = Environment.MachineName;

            // Origin info (required by Home Assistant MQTT Discovery spec)
            var origin = new
            {
                name = "LGSTrayBattery",
                sw = _sw_version,
                url = "https://github.com/strain08/LGSTrayBattery"
            };

            // Shared device registry info
            // Device name without machine suffix ensures same physical device = same HA entity
            // Machine name is available in state JSON attributes (machine_name field)
            var deviceRegistry = new
            {
                identifiers = new[] { $"lgstray_{deviceId}" },
                name = $"{device.DeviceName}_{device.DeviceSignature[^4..]}",
                model = device.DeviceType.ToString(),
                manufacturer = "Logitech",
                sw_version = $"LGSTrayBattery {_sw_version}",
                via_device = machineName
            };

            // 1. Battery Level Sensor Config
            var batteryConfig = new Dictionary<string, object>
            {
                ["name"] = "Battery",
                ["unique_id"] = $"lgstray_{deviceId}_battery",
                ["state_topic"] = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state",
                ["device_class"] = "battery",
                ["state_class"] = "measurement", // Enable long-term stats
                ["unit_of_measurement"] = "%",
                ["value_template"] = "{{ value_json.percentage }}",
                ["json_attributes_topic"] = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state",
                ["device"] = deviceRegistry,
                ["origin"] = origin,
                ["icon"] = GetDeviceIcon(device.DeviceType)
            };

            // 2. Charging Status Binary Sensor Config
            var chargingConfig = new Dictionary<string, object>
            {
                ["name"] = "Charging",
                ["unique_id"] = $"lgstray_{deviceId}_charging",
                ["state_topic"] = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state",
                ["device_class"] = "battery_charging",
                ["value_template"] = "{{ 'ON' if value_json.charging else 'OFF' }}",
                ["device"] = deviceRegistry,
                ["origin"] = origin
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

            lock (_publishLock)
            {
                _publishedDevices.Add(device.DeviceId);
            }

            DiagnosticLogger.Log($"[MQTT] Published discovery config for {device.DeviceName}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[MQTT] Failed to publish discovery config for {device.DeviceName}: {ex.Message}");
        }
    }

    // State Publishing
    private async Task PublishStateUpdateAsync(LogiDevice device)
    {
        try
        {
            var deviceId = SanitizeForMqtt(device.DeviceId);
            var topic = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state";

            var state = new {
                percentage = (int)Math.Round(device.BatteryPercentage),
                voltage = device.BatteryVoltage,
                charging = device.PowerSupplyStatus == PowerSupplyStatus.CHARGING,
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

            DiagnosticLogger.Log($"[MQTT] Published state for {device.DeviceName}: {state.percentage}%");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[MQTT] Failed to publish state for {device.DeviceName}: {ex.Message}");
        }
    }

    // Host Discovery Publishing
    private async Task PublishHostDiscoveryConfigAsync()
    {
        try
        {
            var machineName = Environment.MachineName;
            var deviceId = SanitizeForMqtt(machineName);
            var _sw_version = NotifyIconViewModel.AssemblyVersion;

            // Host Device Registry
            // Identifiers matches 'via_device' of peripherals (which is just machineName)
            var hostDeviceRegistry = new
            {
                identifiers = new[] { machineName },
                name = machineName,
                model = "Host Bridge",
                manufacturer = "LGSTrayBattery",
                sw_version = $"LGSTrayBattery {_sw_version}"
            };

            // Host Status Binary Sensor Config (Connectivity)
            // Uses host-specific status topic so each host has independent connectivity status
            var hostStatusConfig = new Dictionary<string, object>
            {
                ["name"] = "Host Status",
                ["unique_id"] = $"lgstray_{deviceId}_host_status",
                ["state_topic"] = GetHostStatusTopic(),
                ["device_class"] = "connectivity",
                ["payload_on"] = "online",
                ["payload_off"] = "offline",
                ["device"] = hostDeviceRegistry,
                ["origin"] = new
                {
                    name = "LGSTrayBattery",
                    sw = _sw_version,
                    url = "https://github.com/strain08/LGSTrayBattery"
                },
                ["entity_category"] = "diagnostic"
            };

            var payload = System.Text.Json.JsonSerializer.Serialize(hostStatusConfig);
            var topic = $"{_mqttSettings.TopicPrefix}/binary_sensor/{deviceId}/host_status/config";

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(_mqttSettings.RetainMessages)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, CancellationToken.None);

            DiagnosticLogger.Log($"[MQTT] Published host discovery config for {machineName}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[MQTT] Failed to publish host discovery config: {ex.Message}");
        }
    }

    private async Task PublishServiceAvailabilityAsync(bool isOnline)
    {
        try
        {
            var topic = GetHostStatusTopic();
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

    // Helpers

    /// <summary>
    /// Gets the host-specific status topic for LWT and availability.
    /// Format: {TopicPrefix}/host/{MachineName}/status
    /// </summary>
    private string GetHostStatusTopic()
    {
        var machineName = SanitizeForMqtt(Environment.MachineName);
        return $"{_mqttSettings.TopicPrefix}/host/{machineName}/status";
    }

    // Replace invalid MQTT topic characters
    private static string SanitizeForMqtt(string input) => input.Replace(" ", "_")
                                                                .Replace("/", "_")
                                                                .Replace("+", "_")
                                                                .Replace("#", "_");


    private static string GetDeviceIcon(DeviceType deviceType) => deviceType switch {
        DeviceType.Mouse => "mdi:mouse",
        DeviceType.Keyboard => "mdi:keyboard",
        DeviceType.Headset => "mdi:headset",
        _ => "mdi:battery"
    };

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
            DiagnosticLogger.Log("[MQTT] Dispose called");

            if (disposing)
            {
                _reconnectCts?.Cancel();
                _reconnectCts?.Dispose();
                _reconnectCts = null;

                // Unsubscribe from MQTT client events before disposing (may already be done in StopAsync)
                // This prevents event handlers from being called during/after dispose
                // Safe to call -= even if already unsubscribed
                DiagnosticLogger.Log("[MQTT] Ensuring client events are unsubscribed");
                try
                {
                    _mqttClient.ConnectedAsync -= OnConnectedAsync;
                    _mqttClient.DisconnectedAsync -= OnDisconnectedAsync;
                    _mqttClient.ApplicationMessageReceivedAsync -= OnMqttMessageReceivedAsync;
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogWarning($"[MQTT] Error unsubscribing events: {ex.Message}");
                }

                // Dispose MQTT client with timeout to prevent hang
                // StopAsync should have already disconnected it
                DiagnosticLogger.Log("[MQTT] Disposing MQTT client");
                try
                {
                    // Run dispose on thread pool with timeout to prevent deadlock
                    var disposeTask = Task.Run(() => _mqttClient.Dispose());
                    if (!disposeTask.Wait(TimeSpan.FromSeconds(3)))
                    {
                        DiagnosticLogger.LogWarning("[MQTT] Client dispose timed out after 3 seconds - continuing anyway");
                    }
                    else
                    {
                        DiagnosticLogger.Log("[MQTT] MQTT client disposed successfully");
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogWarning($"[MQTT] Error disposing client: {ex.Message}");
                }
            }

            _disposedValue = true;
            DiagnosticLogger.Log("[MQTT] Dispose completed");
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
