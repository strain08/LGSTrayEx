using LGSTrayPrimitives.Interfaces;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace LGSTrayUI.Services;

/// <summary>
/// Production dispatcher implementation that uses WPF's Application.Current.Dispatcher
/// </summary>
public class WpfDispatcher : IDispatcher
{
    public void BeginInvoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return; // Silently ignore during shutdown

        dispatcher.BeginInvoke(action);
    }

    public Task InvokeAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return Task.CompletedTask; // Return completed task during shutdown

        return dispatcher.InvokeAsync(action).Task;
    }
}
