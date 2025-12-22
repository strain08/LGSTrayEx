namespace LGSTrayCore.Interfaces;

/// <summary>
/// Factory for creating WebSocket clients (enables DI and testing)
/// </summary>
public interface IWebSocketClientFactory
{
    /// <summary>
    /// Create a WebSocket client for the given URI
    /// </summary>
    IWebSocketClient Create(Uri uri);
}
