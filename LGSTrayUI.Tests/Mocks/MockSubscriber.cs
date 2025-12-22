using LGSTrayPrimitives.MessageStructs;
using MessagePipe;

namespace LGSTrayUI.Tests.Mocks;

/// <summary>
/// Mock subscriber for IPC messages in tests
/// </summary>
public class MockSubscriber : ISubscriber<IPCMessage>
{
    private readonly List<Action<IPCMessage>> _subscribers = new();

    public IDisposable Subscribe(IMessageHandler<IPCMessage> handler, params MessageHandlerFilter<IPCMessage>[] filters)
    {
        // Not used in our tests
        return new Unsubscriber(() => { });
    }

    public IDisposable Subscribe(Action<IPCMessage> handler, params MessagePipeOptions[] options)
    {
        _subscribers.Add(handler);
        return new Unsubscriber(() => _subscribers.Remove(handler));
    }

    public IDisposable Subscribe(Action<IPCMessage> handler, CancellationToken cancellationToken, params MessagePipeOptions[] options)
    {
        _subscribers.Add(handler);
        return new Unsubscriber(() => _subscribers.Remove(handler));
    }

    /// <summary>
    /// Publish a message to all subscribers
    /// </summary>
    public void PublishToSubscribers(IPCMessage message)
    {
        foreach (var subscriber in _subscribers)
        {
            subscriber(message);
        }
    }

    private class Unsubscriber : IDisposable
    {
        private readonly Action _onDispose;

        public Unsubscriber(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            _onDispose();
        }
    }
}
