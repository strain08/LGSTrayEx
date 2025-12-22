using Hardcodet.Wpf.TaskbarNotification;
using LGSTrayCore;
using LGSTrayPrimitives;
using LGSTrayUI.Interfaces;
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
    private bool disposedValue;
    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // Unsubscribe from static event to prevent memory leak
                if (_themeChangedHandler != null)
                {
                    CheckTheme.StaticPropertyChanged -= _themeChangedHandler;
                    _themeChangedHandler = null;
                }

                SubRef();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
            taskbarIcon.Dispose();
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
        _refCount++;
        RefCountChanged?.Invoke(RefCount);
    }

    public static void SubRef()
    {
        _refCount--;
        RefCountChanged?.Invoke(RefCount);
    }

    public static event Action<int>? RefCountChanged;

    private Action<TaskbarIcon, LogiDevice> _drawBatteryIcon;
    private PropertyChangedEventHandler? _themeChangedHandler;

    public LogiDeviceIcon(LogiDevice device, AppSettings appSettings, UserSettingsWrapper userSettings)
    {
        InitializeComponent();

        if (!appSettings.UI.EnableRichToolTips)
            taskbarIcon.TrayToolTip = null;

        AddRef();

        DataContext = device;

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
        // Don't draw if disposed or taskbarIcon is null
        if (disposedValue || taskbarIcon == null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            // Double-check inside dispatcher callback
            if (!disposedValue && taskbarIcon != null)
            {
                _drawBatteryIcon(taskbarIcon, (LogiDevice)DataContext);
            }
        });
    }
}
