using LGSTrayPrimitives.Interfaces;
using System;
using System.Threading.Tasks;

namespace LGSTrayUI.Tests.Mocks;

/// <summary>
/// Synchronous dispatcher for testing that executes actions immediately on the current thread
/// </summary>
public class SynchronousDispatcher : IDispatcher
{
    public void BeginInvoke(Action action)
    {
        action();
    }

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
