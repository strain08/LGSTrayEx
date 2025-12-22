using MessagePipe;

namespace LGSTrayCore.Tests.Mocks;

/// <summary>
/// Mock publisher for testing IPC message publishing
/// </summary>
public class MockPublisher<TMessage> : IPublisher<TMessage>
{
    public List<TMessage> PublishedMessages { get; } = new();

    public void Publish(TMessage message)
    {
        PublishedMessages.Add(message);
    }
}
