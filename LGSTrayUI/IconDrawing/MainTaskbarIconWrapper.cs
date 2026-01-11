using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Threading;
using System.Windows;

namespace LGSTrayUI.IconDrawing;

/// <summary>
/// Manages the application's main taskbar icon, automatically showing or hiding it based on the presence of device
/// icons.
/// </summary>
/// <remarks>This class listens for changes in device icon reference counts and updates the main taskbar icon
/// accordingly. It implements a debounce mechanism to avoid unnecessary creation or disposal of the icon during rapid
/// changes. Callers should dispose of this object when it is no longer needed to release resources and unsubscribe from
/// events.</remarks>
public class MainTaskbarIconWrapper : IDisposable
{
    #region IDisposable
    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                _debounceTimer?.Dispose();
                _taskbarIcon?.Dispose();
                LogiDeviceIcon.RefCountChanged -= OnRefCountChanged;
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~MainTaskbarIconWrapper()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion

    private TaskbarIcon? _taskbarIcon = new MainTaskBarIcon();
    private Timer? _debounceTimer;
    private readonly object _timerLock = new();
    private int _timerVersion = 0;  // Track timer generations to detect stale callbacks
    private object? _contextMenuDataContext;  // Store DataContext for ContextMenus

    public MainTaskbarIconWrapper()
    {
        LogiDeviceIcon.RefCountChanged += OnRefCountChanged;
        OnRefCountChanged(LogiDeviceIcon.RefCount);
    }

    /// <summary>
    /// Sets the DataContext for the ContextMenu on the main taskbar icon.
    /// This should be called after construction to bind the ContextMenu to the NotifyIconViewModel.
    /// </summary>
    /// <param name="dataContext">The DataContext to set (typically NotifyIconViewModel)</param>
    public void SetContextMenuDataContext(object dataContext)
    {
        lock (_timerLock)
        {
            _contextMenuDataContext = dataContext;

            // Set DataContext on existing icon's ContextMenu if present
            if (_taskbarIcon?.ContextMenu != null)
            {
                _taskbarIcon.ContextMenu.DataContext = dataContext;
            }
        }
    }

    private void OnRefCountChanged(int refCount)
    {
        lock (_timerLock)
        {
            // Dispose old timer and invalidate its callbacks
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _timerVersion++;  // Increment to invalidate any pending callbacks

            if (refCount != 0)
            {
                // Immediately hide main icon when device icons appear
                _taskbarIcon?.Dispose();
                _taskbarIcon = null;
                return;
            }

            // Debounce: Wait 500ms before showing main icon
            // This prevents unnecessary creation/disposal during rediscover
            var currentVersion = _timerVersion;

            _debounceTimer = new Timer(_ =>
            {
                // Quick check under lock to detect stale timer
                lock (_timerLock)
                {
                    if (currentVersion != _timerVersion)
                        return; // Stale callback, ignore
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    lock (_timerLock)
                    {
                        // Final safety checks with early returns to reduce nesting
                        if (currentVersion != _timerVersion)
                            return;

                        if (LogiDeviceIcon.RefCount != 0)
                            return;

                        if (_taskbarIcon != null)
                            return;

                        _taskbarIcon = new MainTaskBarIcon();

                        // Set DataContext on the new icon's ContextMenu
                        if (_contextMenuDataContext != null && _taskbarIcon.ContextMenu != null)
                        {
                            _taskbarIcon.ContextMenu.DataContext = _contextMenuDataContext;
                        }
                    }
                });
            }, state: null,
               dueTime: 500,
               period: Timeout.Infinite);
        }
    }
}
