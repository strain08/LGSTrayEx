using Websocket.Client;

namespace LGSTrayCore.Interfaces;

/// <summary>
/// Abstraction over WebSocket client for testability
/// </summary>
public interface IWebSocketClient : IDisposable
{
    /// <summary>
    /// Observable stream of messages received from the WebSocket
    /// </summary>
    IObservable<ResponseMessage> MessageReceived { get; }

    /// <summary>
    /// Timeout for reconnection after error (nullable to match WebsocketClient)
    /// </summary>
    TimeSpan? ErrorReconnectTimeout { get; set; }

    /// <summary>
    /// Timeout for reconnection (null = disabled)
    /// </summary>
    TimeSpan? ReconnectTimeout { get; set; }

    /// <summary>
    /// Send a text message through the WebSocket
    /// </summary>
    void Send(string message);

    /// <summary>
    /// Start the WebSocket connection
    /// </summary>
    Task Start();

    /// <summary>
    /// Stop the WebSocket connection
    /// </summary>
    Task Stop();
}
