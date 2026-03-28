using CommunityToolkit.Mvvm.ComponentModel;
using LGSTrayCore;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayUI.Interfaces;
using LGSTrayUI.Services;
using System;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;

namespace LGSTrayUI;

public partial class LogiDeviceViewModel : LogiDevice, IDisposable
{
    private readonly ILogiDeviceIconFactory _logiDeviceIconFactory;
    private readonly AppSettings _appSettings;
    private readonly UserSettingsWrapper _userSettings;
    private readonly PropertyChangedEventHandler _propertyChangedHandler;

    [ObservableProperty]
    private bool _isChecked = false;

    private LogiDeviceIcon? taskbarIcon;
    private string? _cachedDetailedTooltip;
    private readonly ModeSwitchDetector _modeSwitchDetector;

    public LogiDeviceViewModel(ILogiDeviceIconFactory logiDeviceIconFactory, AppSettings appSettings, UserSettingsWrapper userSettings)
    {
        _logiDeviceIconFactory = logiDeviceIconFactory;
        _appSettings = appSettings;
        _userSettings = userSettings;
        _modeSwitchDetector = new ModeSwitchDetector(appSettings.Notifications);

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
            case nameof(BatteryVoltage):
            case nameof(BatteryMileage):
            case nameof(LastUpdate):
                _cachedDetailedTooltip = null;  // Invalidate tooltip cache
                OnPropertyChanged(nameof(DetailedMenuTooltip));
                break;
            case nameof(BatteryPercentage):
                _cachedDetailedTooltip = null;
                OnPropertyChanged(nameof(DetailedMenuTooltip));
                break;
            case nameof(IsOnline):
                // IsOnline is used for notifications - icon visibility uses IsVisuallyOnline
                break;
            case nameof(IsVisuallyOnline):
                // Visual state controls icon visibility (with grace period)
                UpdateIconVisibility();
                break;
        }
    }

    partial void OnIsCheckedChanged(bool oldValue, bool newValue)
    {
        UpdateIconVisibility();
    }

    public void UpdateIconVisibility()
    {
        bool isOffline = !IsVisuallyOnline;  // Use visual state (with grace period)
        bool shouldShow = IsChecked && (!isOffline || _userSettings.KeepOfflineDevices);

        if (shouldShow)
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

        // Derive stable signature: GHub devices already carry "GHUB." prefix as their ID;
        // Native devices get the "NATIVE.{type}.{id}" format for backward compat with saved settings.
        DeviceSignature = DataSource == DataSource.GHub
            ? initMessage.deviceId
            : $"NATIVE.{initMessage.deviceType}.{initMessage.deviceId}";
    }

    public void UpdateState(UpdateMessage updateMessage)
    {
        if (updateMessage.batteryPercentage >= 0 || updateMessage.IsWiredMode)
        {
            // Device is online or in wired mode - cancel any pending offline timer
            _modeSwitchDetector.SetOnline(DeviceName);

            IsOnline = true;
            IsVisuallyOnline = true;

            if (updateMessage.batteryPercentage >= 0)
            {
                BatteryPercentage = updateMessage.batteryPercentage;
            }
        }
        else
        {
            // CRITICAL: Update IsOnline IMMEDIATELY (for notifications)
            IsOnline = false;

            // Delay IsVisuallyOnline (for icon rendering - prevents "?" flicker during mode switch)
            _modeSwitchDetector.SetOffline(DeviceName, () =>
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => IsVisuallyOnline = false));
        }

        PowerSupplyStatus = updateMessage.powerSupplyStatus;
        BatteryVoltage = updateMessage.batteryMVolt / 1000.0;
        BatteryMileage = updateMessage.Mileage;
        LastUpdate = updateMessage.updateTime;
        IsWiredMode = updateMessage.IsWiredMode;
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
                var wiredString = IsWiredMode ? "Wired" : "Wireless";
                sb.AppendLine($"Mode: {wiredString}");

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

        _modeSwitchDetector.Dispose();

        // Dispose icon if it was created
        if (IsChecked)
        {
            taskbarIcon?.Dispose();
            taskbarIcon = null;
        }
    }
}
