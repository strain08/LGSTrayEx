using CommunityToolkit.Mvvm.ComponentModel;
using LGSTrayCore;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayUI.Interfaces;
using System;
using System.ComponentModel;
using System.Text;

namespace LGSTrayUI;

public class LogiDeviceViewModelFactory
{
    private readonly ILogiDeviceIconFactory _logiDeviceIconFactory;

    public LogiDeviceViewModelFactory(ILogiDeviceIconFactory logiDeviceIconFactory)
    {
        _logiDeviceIconFactory = logiDeviceIconFactory;
    }

    public LogiDeviceViewModel CreateViewModel(Action<LogiDeviceViewModel>? config = null)
    {
        LogiDeviceViewModel output = new(_logiDeviceIconFactory);
        config?.Invoke(output);

        return output;
    }
}

public partial class LogiDeviceViewModel : LogiDevice, IDisposable
{
    private readonly ILogiDeviceIconFactory _logiDeviceIconFactory;
    private readonly PropertyChangedEventHandler _propertyChangedHandler;

    [ObservableProperty]
    private bool _isChecked = false;

    /// <summary>
    /// Stable device signature for persistent settings (GHUB.xxx or NATIVE.xxx)
    /// </summary>
    [ObservableProperty]
    private string _deviceSignature = string.Empty;

    private LogiDeviceIcon? taskbarIcon;
    private string? _cachedDetailedTooltip;

    public LogiDeviceViewModel(ILogiDeviceIconFactory logiDeviceIconFactory)
    {
        _logiDeviceIconFactory = logiDeviceIconFactory;

        // Subscribe to property changes from base class to update computed properties
        // Store handler reference for later unsubscription (prevents memory leak)
        _propertyChangedHandler = OnPropertyChangedInternal;
        PropertyChanged += _propertyChangedHandler;
    }

    private void OnPropertyChangedInternal(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DataSource):
                _cachedDetailedTooltip = null;  // Invalidate tooltip cache
                OnPropertyChanged(nameof(DataSourceDisplayName));
                OnPropertyChanged(nameof(BadgeLetter));
                OnPropertyChanged(nameof(DetailedMenuTooltip));
                break;
            case nameof(DeviceName):
            case nameof(DeviceId):
            case nameof(DeviceSignature):
            case nameof(BatteryPercentage):
            case nameof(BatteryVoltage):
            case nameof(BatteryMileage):
            case nameof(LastUpdate):
                _cachedDetailedTooltip = null;  // Invalidate tooltip cache
                OnPropertyChanged(nameof(DetailedMenuTooltip));
                break;
        }
    }

    partial void OnIsCheckedChanged(bool oldValue, bool newValue)
    {
        if (newValue)
        {
            taskbarIcon ??= _logiDeviceIconFactory.CreateDeviceIcon(this);
        }
        else
        {
            taskbarIcon?.Dispose();
            taskbarIcon = null;
        }
    }

    public void UpdateState(InitMessage initMessage)
    {
        if (string.IsNullOrEmpty(DeviceId) || DeviceId == NOT_FOUND)
        {
            DeviceId = initMessage.deviceId;
        }

        DeviceName = initMessage.deviceName;
        HasBattery = initMessage.hasBattery;
        DeviceType = initMessage.deviceType;
        DataSource = DataSourceHelper.GetDataSource(initMessage.deviceId);

        // Store signature for persistent settings
        if (!string.IsNullOrEmpty(initMessage.deviceSignature))
        {
            DeviceSignature = initMessage.deviceSignature;
        }
        else
        {
            // Fallback: use deviceId as signature if none provided
            DeviceSignature = initMessage.deviceId;
        }
    }

    public void UpdateState(UpdateMessage updateMessage)
    {
        BatteryPercentage = updateMessage.batteryPercentage;
        PowerSupplyStatus = updateMessage.powerSupplyStatus;
        BatteryVoltage = updateMessage.batteryMVolt / 1000.0;
        BatteryMileage = updateMessage.Mileage;
        LastUpdate = updateMessage.updateTime;
    }

    /// <summary>
    /// Human-readable data source name for display
    /// </summary>
    public string DataSourceDisplayName => DataSource switch
    {
        DataSource.Native => "Native HID++ 2.0",
        DataSource.GHub => "Logitech G Hub",
        _ => "Unknown"
    };

    /// <summary>
    /// Badge letter for menu display ("N" or "G")
    /// </summary>
    public string BadgeLetter => DataSource switch
    {
        DataSource.Native => "N",
        DataSource.GHub => "G",
        _ => "?"
    };

    /// <summary>
    /// Detailed tooltip for menu items (cached for performance)
    /// </summary>
    public string DetailedMenuTooltip
    {
        get
        {
            if (_cachedDetailedTooltip == null)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Device: {DeviceName}");
                sb.AppendLine($"Source: {DataSourceDisplayName}");
                sb.AppendLine($"ID: {DeviceId}");

                // Show signature for debugging/troubleshooting
                if (!string.IsNullOrEmpty(DeviceSignature))
                    sb.AppendLine($"Signature: {DeviceSignature}");

                if (LastUpdate != DateTimeOffset.MinValue)
                    sb.AppendLine($"Last Update: {LastUpdate:g}");

                if (DataSource == DataSource.Native && BatteryVoltage > 0)
                    sb.AppendLine($"Voltage: {BatteryVoltage:F2}V");

                if (DataSource == DataSource.GHub && BatteryMileage > 0)
                    sb.AppendLine($"Mileage: {BatteryMileage:F1}h");

                _cachedDetailedTooltip = sb.ToString().TrimEnd();
            }

            return _cachedDetailedTooltip;
        }
    }

    public void Dispose()
    {
        // Unsubscribe from PropertyChanged event to prevent memory leak
        PropertyChanged -= _propertyChangedHandler;

        // Dispose icon if it was created
        if (IsChecked)
        {
            taskbarIcon?.Dispose();
            taskbarIcon = null;
        }
    }
}
