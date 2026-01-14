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

        // Unregister from messenger
        _batterySubscription?.Dispose();
        _suspendSubscription?.Dispose();
        _republishSubscription?.Dispose();

        // Cancel reconnection attempts
        _reconnectCts?.Cancel();

        // Disconnect gracefully
        DiagnosticLogger.Log($"[MQTT] Client connected: {_mqttClient.IsConnected}");

        if (_mqttClient.IsConnected)
        {
            try
            {
                DiagnosticLogger.Log("[MQTT] Starting graceful shutdown sequence...");

                // Mark all monitored devices as disconnected (state update only)
                // This updates the Status binary sensor to show "Not Connected"
                // while keeping devices available in HA (not grayed out)
                DiagnosticLogger.Log("[MQTT] Calling MarkAllDevicesOfflineAsync...");
                await MarkAllDevicesOfflineAsync();
                DiagnosticLogger.Log("[MQTT] MarkAllDevicesOfflineAsync completed");

                // Note: We do NOT publish service offline status during graceful shutdown
                // This keeps devices "available" in HA with just the Status sensor showing disconnected
                // LWT will handle marking service offline if app crashes

                DiagnosticLogger.Log("[MQTT] Disconnecting from broker...");
                await _mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
                DiagnosticLogger.Log("[MQTT] Disconnected from broker");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError($"[MQTT] Error during disconnect: {ex.Message}");
                DiagnosticLogger.LogError($"[MQTT] Stack trace: {ex.StackTrace}");
            }
        }
        else
        {
            DiagnosticLogger.Log("[MQTT] Client not connected, skipping graceful shutdown");
        }

        //return Task.CompletedTask;
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
                if (_mqttSettings.PublishLWT) {
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

        // Subscribe to Home Assistant birth message
        var birthTopic = $"{_mqttSettings.TopicPrefix}/status";
        try
        {
            await _mqttClient.SubscribeAsync(birthTopic);
            DiagnosticLogger.Log($"[MQTT] Subscribed to HA birth topic: {birthTopic}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"[MQTT] Failed to subscribe to HA birth topic: {ex.Message}");
        }

        // Publish Service Availability (Birth Message)
        _ = PublishServiceAvailabilityAsync(true);

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

        // Check if this is Home Assistant birth message
        var birthTopic = $"{_mqttSettings.TopicPrefix}/status";
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
        DiagnosticLogger.Log("[MQTT] System suspending - marking all devices as disconnected");

        // Check connection
        if (!_mqttClient.IsConnected)
        {
            DiagnosticLogger.Log("[MQTT] Not connected, skipping device offline marking");
            return;
        }

        // Mark all devices as disconnected before system sleeps
        // Fire-and-forget since we can't wait in the suspend handler
        _ = MarkAllDevicesOfflineAsync();
    }

    public void OnBatteryUpdate(DeviceBatteryUpdatedMessage message)
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
                name = device.DeviceName,
                model = device.DeviceType.ToString(),
                manufacturer = "Logitech",
                sw_version = $"LGSTrayBattery {_sw_version}",
                via_device = machineName
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
                origin,
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
                origin,
                availability = availabilityConfig,
                availability_mode = "all"
            };

            // 3. Connectivity Binary Sensor Config
            var connectivityConfig = new
            {
                name = $"{device.DeviceName} Status",
                unique_id = $"lgstray_{deviceId}_connectivity",
                state_topic = $"{_mqttSettings.TopicPrefix}/sensor/{deviceId}/battery/state",
                device_class = "connectivity",
                value_template = "{{ 'ON' if value_json.online else 'OFF' }}",
                device = deviceRegistry,
                origin,
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

    private async Task MarkAllDevicesOfflineAsync()
    {
        try
        {
            DiagnosticLogger.Log("[MQTT] MarkAllDevicesOfflineAsync - ENTERED");

            // Get all devices that were published (tracked in _publishedDevices)
            List<string> devicesToMarkOffline;
            lock (_publishLock)
            {
                devicesToMarkOffline = new List<string>(_publishedDevices);
                DiagnosticLogger.Log($"[MQTT] Found {devicesToMarkOffline.Count} published devices to mark offline");
            }

            if (devicesToMarkOffline.Count == 0)
            {
                DiagnosticLogger.Log("[MQTT] No devices to mark offline");
                return;
            }

            // Publish state update with online=false for each device
            foreach (var deviceId in devicesToMarkOffline)
            {
                DiagnosticLogger.Log($"[MQTT] Marking device {deviceId} as disconnected...");
                await PublishDeviceOfflineStateAsync(deviceId);
                DiagnosticLogger.Log($"[MQTT] Device {deviceId} marked as disconnected");
            }

            DiagnosticLogger.Log($"[MQTT] Successfully marked {devicesToMarkOffline.Count} device(s) as disconnected");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[MQTT] Failed to mark devices offline: {ex.Message}");
            DiagnosticLogger.LogError($"[MQTT] Stack trace: {ex.StackTrace}");
        }
    }

    private async Task PublishDeviceOfflineStateAsync(string deviceId)
    {
        try
        {
            var sanitizedId = SanitizeForMqtt(deviceId);
            var topic = $"{_mqttSettings.TopicPrefix}/sensor/{sanitizedId}/battery/state";

            // Publish minimal state update with online=false
            // This updates the connectivity binary sensor to show "OFF"
            // while keeping the device available in HA
            // Note: We only send 'online' field - battery percentage and charging status
            // retain their last values (don't reset to 0%)
            var state = new
            {
                online = false,
                last_update = DateTimeOffset.Now.ToString("o")
            };

            var payload = System.Text.Json.JsonSerializer.Serialize(state);

            DiagnosticLogger.Log($"[MQTT] Publishing offline state to topic: {topic}");
            DiagnosticLogger.Log($"[MQTT] Payload: {payload}");

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(_mqttSettings.RetainMessages)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            _mqttClient.PublishAsync(message).Wait(TimeSpan.FromSeconds(2));
            DiagnosticLogger.Log($"[MQTT] Successfully published offline state for {deviceId}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[MQTT] Failed to publish offline state for device {deviceId}: {ex.Message}");
            DiagnosticLogger.LogError($"[MQTT] Stack trace: {ex.StackTrace}");
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
                
                //_mqttClient?.Dispose();
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
