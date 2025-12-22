using LGSTrayCore.Interfaces;
using System.Net.WebSockets;
using Websocket.Client;

namespace LGSTrayCore.WebSocket;

/// <summary>
/// Production factory that creates real WebSocket clients with GHUB headers
/// </summary>
public class WebSocketClientFactory : IWebSocketClientFactory
{
    public IWebSocketClient Create(Uri uri)
    {
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

        var websocketClient = new WebsocketClient(uri, factory);
        return new WebSocketClientAdapter(websocketClient);
    }
}
