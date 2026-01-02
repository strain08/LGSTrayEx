using Hardcodet.Wpf.TaskbarNotification;
using LGSTrayCore;
using LGSTrayPrimitives;
using LGSTrayUI.Interfaces;
using LGSTrayUI.Services;
using Microsoft.Extensions.Options;
using System;
using System.ComponentModel;
using System.Windows.Controls;

namespace LGSTrayUI;

public class LogiDeviceIconFactory : ILogiDeviceIconFactory
{
    private readonly AppSettings _appSettings;
    private readonly UserSettingsWrapper _userSettings;

    public LogiDeviceIconFactory(IOptions<AppSettings> appSettings, UserSettingsWrapper userSettings)
    {
        _appSettings = appSettings.Value;
        _userSettings = userSettings;
    }

    public LogiDeviceIcon CreateDeviceIcon(LogiDevice device, Action<LogiDeviceIcon>? config = null)
    {
        LogiDeviceIcon output = new(device, _appSettings, _userSettings);
        config?.Invoke(output);

        return output;
    }
}

public partial class LogiDeviceIcon : UserControl, IDisposable
{
    #region IDisposable
    private volatile int _disposed = 0;  // 0 = not disposed, 1 = disposed (atomic flag)
    private readonly object _disposeLock = new object();

    protected virtual void Dispose(bool disposing)
    {
        // Atomic check-and-set: only first caller proceeds
        if (System.Threading.Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
            return;  // Already disposed by another thread

        lock (_disposeLock)
        {
            if (disposing)
            {
                // Unsubscribe from all event handlers to prevent memory leaks
                if (_subscribedDevice != null)
                {
                    _subscribedDevice.PropertyChanged -= LogiDevicePropertyChanged;
                    _subscribedDevice = null;
                }

                if (_subscribedSettings != null)
                {
                    _subscribedSettings.PropertyChanged -= NotifyIconViewModelPropertyChanged;
                    _subscribedSettings = null;
                }

                if (_themeChangedHandler != null)
                {
                    CheckTheme.StaticPropertyChanged -= _themeChangedHandler;
                    _themeChangedHandler = null;
                }

                SubRef();
            }

            // Dispose taskbar icon last
            taskbarIcon?.Dispose();
            taskbarIcon = null;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~LogiDeviceIcon()
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

    private static int _refCount = 0;
    public static int RefCount => _refCount;

    public static void AddRef()
    {
        var newCount = System.Threading.Interlocked.Increment(ref _refCount);

        // Capture delegate to avoid null-check race
        var handler = RefCountChanged;
        handler?.Invoke(newCount);
    }

    public static void SubRef()
    {
        var newCount = System.Threading.Interlocked.Decrement(ref _refCount);

        // Capture delegate to avoid null-check race
        var handler = RefCountChanged;
        handler?.Invoke(newCount);
    }

    public static event Action<int>? RefCountChanged;

    private Action<TaskbarIcon, LogiDevice> _drawBatteryIcon;
    private PropertyChangedEventHandler? _themeChangedHandler;

    // Store references to subscribed objects for later unsubscription
    private LogiDevice? _subscribedDevice;
    private UserSettingsWrapper? _subscribedSettings;

    public LogiDeviceIcon(LogiDevice device, AppSettings appSettings, UserSettingsWrapper userSettings)
    {
        InitializeComponent();

        if (!appSettings.UI.EnableRichToolTips)
            taskbarIcon.TrayToolTip = null;

        AddRef();

        DataContext = device;

        // Store references for later unsubscription (prevents memory leaks)
        _subscribedDevice = device;
        _subscribedSettings = userSettings;

        device.PropertyChanged += LogiDevicePropertyChanged;
        userSettings.PropertyChanged += NotifyIconViewModelPropertyChanged;

        // Store handler to unsubscribe later
        _themeChangedHandler = (_, _) => DrawBatteryIcon();
        CheckTheme.StaticPropertyChanged += _themeChangedHandler;

        _drawBatteryIcon = userSettings.NumericDisplay ? BatteryIconDrawing.DrawNumeric : BatteryIconDrawing.DrawIcon;
        DrawBatteryIcon();
    }

    private void NotifyIconViewModelPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (s is not UserSettingsWrapper userSettings)
        {
            return;
        }

        if (e.PropertyName == nameof(UserSettingsWrapper.NumericDisplay))
        {
            _drawBatteryIcon = userSettings.NumericDisplay ? BatteryIconDrawing.DrawNumeric : BatteryIconDrawing.DrawIcon;
            DrawBatteryIcon();
        }
    }

    private void LogiDevicePropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (s is not LogiDevice)
        {
            return;
        }
        else if (e.PropertyName is nameof(LogiDevice.BatteryPercentage) or nameof(LogiDevice.PowerSupplyStatus))
        {
            DrawBatteryIcon();
        }
    }

    private void DrawBatteryIcon()
    {
        // Quick check before acquiring lock
        if (_disposed == 1)
            return;

        // Capture taskbarIcon under lock to ensure thread safety
        TaskbarIcon? iconSnapshot;
        lock (_disposeLock)
        {
            if (_disposed == 1 || taskbarIcon == null)
                return;

            iconSnapshot = taskbarIcon;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            // Final check inside dispatcher callback
            if (_disposed == 1)
                return;

            lock (_disposeLock)
            {
                if (_disposed == 1 || taskbarIcon == null)
                    return;

                _drawBatteryIcon(taskbarIcon, (LogiDevice)DataContext);
            }
        });
    }
}
