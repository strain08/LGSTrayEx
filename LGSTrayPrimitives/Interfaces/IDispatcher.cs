namespace LGSTrayPrimitives.Interfaces;

/// <summary>
/// Abstraction over UI dispatcher for testability
/// </summary>
public interface IDispatcher
{
    /// <summary>
    /// Execute action asynchronously on the UI thread
    /// </summary>
    void BeginInvoke(Action action);

    /// <summary>
    /// Execute action on the UI thread and wait for completion
    /// </summary>
    Task InvokeAsync(Action action);
}
