using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Windows;

namespace LGSTrayUI;

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
    private System.Threading.Timer? _debounceTimer;
    private readonly object _timerLock = new object();
    private int _timerVersion = 0;  // Track timer generations to detect stale callbacks

    public MainTaskbarIconWrapper()
    {
        LogiDeviceIcon.RefCountChanged += OnRefCountChanged;
        OnRefCountChanged(LogiDeviceIcon.RefCount);
    }

    private void OnRefCountChanged(int refCount)
    {
        lock (_timerLock)
        {
            // Dispose old timer and invalidate its callbacks
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _timerVersion++;  // Increment to invalidate any pending callbacks

            if (refCount == 0)
            {
                // Debounce: Wait 500ms before showing main icon
                // This prevents unnecessary creation/disposal during rediscover
                var currentVersion = _timerVersion;

                _debounceTimer = new System.Threading.Timer(_ =>
                {
                    // Check if this timer was cancelled before acquiring lock
                    bool isStale;
                    lock (_timerLock)
                    {
                        isStale = currentVersion != _timerVersion;
                    }

                    if (isStale)
                        return;  // Stale callback, ignore

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        lock (_timerLock)
                        {
                            // Final check: ensure timer is still valid and count is still 0
                            if (currentVersion == _timerVersion &&
                                LogiDeviceIcon.RefCount == 0 &&
                                _taskbarIcon == null)
                            {
                                _taskbarIcon = new MainTaskBarIcon();
                            }
                        }
                    });
                }, null, 500, System.Threading.Timeout.Infinite);
            }
            else
            {
                // Immediately hide main icon when device icons appear
                _taskbarIcon?.Dispose();
                _taskbarIcon = null;
            }
        }
    }
}
