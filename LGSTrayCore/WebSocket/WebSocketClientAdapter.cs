using LGSTrayCore.Interfaces;
using Websocket.Client;

namespace LGSTrayCore.WebSocket;

/// <summary>
/// Adapter that wraps Websocket.Client.WebsocketClient to implement IWebSocketClient interface
/// </summary>
public class WebSocketClientAdapter : IWebSocketClient
{
    private readonly WebsocketClient _client;
    private bool _disposed;

    public WebSocketClientAdapter(WebsocketClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public IObservable<ResponseMessage> MessageReceived => _client.MessageReceived;

    public TimeSpan? ErrorReconnectTimeout
    {
        get => _client.ErrorReconnectTimeout;
        set => _client.ErrorReconnectTimeout = value;
    }

    public TimeSpan? ReconnectTimeout
    {
        get => _client.ReconnectTimeout;
        set => _client.ReconnectTimeout = value;
    }

    public void Send(string message) => _client.Send(message);

    public async Task Start() => await _client.Start();

    public async Task Stop() => await _client.Stop(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing");

    public void Dispose()
    {
        if (!_disposed)
        {
            _client?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
