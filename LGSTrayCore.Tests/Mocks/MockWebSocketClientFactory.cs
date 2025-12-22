using LGSTrayCore.Interfaces;

namespace LGSTrayCore.Tests.Mocks;

/// <summary>
/// Mock factory for creating MockWebSocketClient instances in tests
/// </summary>
public class MockWebSocketClientFactory : IWebSocketClientFactory
{
    private readonly MockWebSocketClient _mockClient;

    public MockWebSocketClientFactory(MockWebSocketClient mockClient)
    {
        _mockClient = mockClient;
    }

    public IWebSocketClient Create(Uri uri)
    {
        return _mockClient;
    }
}
