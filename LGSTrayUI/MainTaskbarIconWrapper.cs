using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Windows;

namespace LGSTrayUI;

public class MainTaskBarIcon : TaskbarIcon
{
    public MainTaskBarIcon() : base()
    {
        ContextMenu = (System.Windows.Controls.ContextMenu) Application.Current.FindResource("SysTrayMenu");
        BatteryIconDrawing.DrawUnknown(this);
    }
}

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

    public MainTaskbarIconWrapper()
    {
        LogiDeviceIcon.RefCountChanged += OnRefCountChanged;
        OnRefCountChanged(LogiDeviceIcon.RefCount);
    }

    private void OnRefCountChanged(int refCount)
    {
        // Cancel any pending timer
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        if (refCount == 0)
        {
            // Debounce: Wait 500ms before showing main icon
            // This prevents unnecessary creation/disposal during rediscover
            _debounceTimer = new System.Threading.Timer(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // Double-check count hasn't changed
                    if (LogiDeviceIcon.RefCount == 0)
                    {
                        _taskbarIcon ??= new MainTaskBarIcon();
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
