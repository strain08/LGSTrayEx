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
        Application.Current.Dispatcher.BeginInvoke(action);
    }

    public Task InvokeAsync(Action action)
    {
        return Application.Current.Dispatcher.InvokeAsync(action).Task;
    }
}
