using LGSTrayCore.Interfaces;
using Newtonsoft.Json;
using System.Reactive.Subjects;
using Websocket.Client;

namespace LGSTrayCore.Tests.Mocks;

/// <summary>
/// Mock WebSocket client for testing GHUB manager without actual WebSocket connection
/// </summary>
public class MockWebSocketClient : IWebSocketClient
{
    private Subject<ResponseMessage> _messageSubject = new();
    private bool _disposed;

    public IObservable<ResponseMessage> MessageReceived
    {
        get
        {
            // Recreate Subject if disposed (handles RediscoverDevices scenario)
            if (_disposed || _messageSubject == null)
            {
                _messageSubject = new Subject<ResponseMessage>();
                _disposed = false;
            }
            return _messageSubject;
        }
    }

    public List<string> SentMessages { get; } = new();

    public TimeSpan? ReconnectTimeout { get; set; }
    public TimeSpan? ErrorReconnectTimeout { get; set; }

    public void Send(string message) => SentMessages.Add(message);

    public Task Start()
    {
        // If we were disposed, recreate the subject
        if (_disposed)
        {
            _messageSubject = new Subject<ResponseMessage>();
            _disposed = false;
        }
        return Task.CompletedTask;
    }

    public Task Stop() => Task.CompletedTask;

    public void Dispose()
    {
        if (!_disposed)
        {
            _messageSubject?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    // Test helper methods

    /// <summary>
    /// Simulate a GHUB message with the given path and payload
    /// </summary>
    public void SimulateMessage(string path, object payload)
    {
        var ghubMsg = new
        {
            msgId = "",
            verb = "GET",
            path,
            payload
        };
        var json = JsonConvert.SerializeObject(ghubMsg);
        _messageSubject.OnNext(ResponseMessage.TextMessage(json));
    }

    /// <summary>
    /// Simulate GHUB /devices/list response
    /// </summary>
    public void SimulateDeviceListResponse(params (string id, string name, bool hasBattery)[] devices)
    {
        var deviceInfos = devices.Select(d => new
        {
            id = d.id,
            extendedDisplayName = d.name,
            deviceType = "mouse",
            capabilities = new { hasBatteryStatus = d.hasBattery }
        }).ToArray();

        SimulateMessage("/devices/list", new { deviceInfos });
    }

    /// <summary>
    /// Simulate GHUB /devices/state/changed event
    /// </summary>
    public void SimulateDeviceStateChange(string deviceId, string state)
    {
        SimulateMessage("/devices/state/changed", new { deviceId, state });
    }

    /// <summary>
    /// Simulate GHUB /battery/state/changed event
    /// </summary>
    public void SimulateBatteryUpdate(string deviceId, int percentage, bool charging, double mileage = 0)
    {
        SimulateMessage("/battery/state/changed", new { deviceId, percentage, charging, mileage });
    }
}
