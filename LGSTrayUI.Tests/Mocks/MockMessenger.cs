using CommunityToolkit.Mvvm.Messaging;

namespace LGSTrayUI.Tests.Mocks;

/// <summary>
/// Mock messenger for testing - does nothing but satisfies IMessenger interface
/// </summary>
public class MockMessenger : IMessenger
{
    public List<object> SentMessages { get; } = new();

    public bool IsRegistered<TMessage, TToken>(object recipient, TToken token) where TMessage : class where TToken : IEquatable<TToken>
    {
        return false;
    }

    public void Register<TRecipient, TMessage, TToken>(TRecipient recipient, TToken token, MessageHandler<TRecipient, TMessage> handler) where TRecipient : class where TMessage : class where TToken : IEquatable<TToken>
    {
        // No-op for testing
    }

    public TMessage Send<TMessage, TToken>(TMessage message, TToken token) where TMessage : class where TToken : IEquatable<TToken>
    {
        SentMessages.Add(message);
        return message;
    }

    public void Unregister<TMessage, TToken>(object recipient, TToken token) where TMessage : class where TToken : IEquatable<TToken>
    {
        // No-op for testing
    }

    public void UnregisterAll(object recipient)
    {
        // No-op for testing
    }

    public void UnregisterAll<TToken>(object recipient, TToken token) where TToken : IEquatable<TToken>
    {
        // No-op for testing
    }

    public void Reset()
    {
        // No-op for testing
    }

    public void Cleanup()
    {
        // No-op for testing
    }
}
