using LGSTrayCore;
using System;

namespace LGSTrayUI.Interfaces;

/// <summary>
/// Factory interface for creating LogiDeviceIcon instances
/// Enables mocking in tests to avoid WPF STA thread requirements
/// </summary>
public interface ILogiDeviceIconFactory
{
    /// <summary>
    /// Sets the DataContext to be used for ContextMenus on device icons.
    /// This should be called with the NotifyIconViewModel after it's constructed.
    /// </summary>
    /// <param name="dataContext">The DataContext (typically NotifyIconViewModel)</param>
    void SetContextMenuDataContext(object dataContext);

    /// <summary>
    /// Creates a LogiDeviceIcon for the specified device
    /// </summary>
    /// <param name="device">The device to create an icon for</param>
    /// <param name="config">Optional configuration action</param>
    /// <returns>A LogiDeviceIcon instance, or null if creation is not needed</returns>
    LogiDeviceIcon? CreateDeviceIcon(LogiDevice device, Action<LogiDeviceIcon>? config = null);
}
