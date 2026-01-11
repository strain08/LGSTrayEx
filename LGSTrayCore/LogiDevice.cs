using CommunityToolkit.Mvvm.ComponentModel;
using LGSTrayPrimitives;

namespace LGSTrayCore;

public partial class LogiDevice : ObservableObject
{
    public const string NOT_FOUND = "NOT FOUND";

    [ObservableProperty]
    private DeviceType _deviceType;

    [ObservableProperty]
    private DataSource _dataSource = DataSource.Native;

    [ObservableProperty]
    private string _deviceId = NOT_FOUND;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipString))]
    private string _deviceName = NOT_FOUND;

    [ObservableProperty]
    private bool _hasBattery = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipString))]
    private double _batteryPercentage = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipString))]
    private double _batteryVoltage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipString))]
    private double _batteryMileage;


    [ObservableProperty]
    private PowerSupplyStatus _powerSupplyStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipString))]
    private DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipString))]
    private bool _isWiredMode = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipString))]
    private bool _isOnline = false;

    /// <summary>
    /// Visual representation of online state with grace period for mode-switch detection.
    /// Used exclusively for icon rendering to prevent "?" flicker during brief disconnections.
    /// For notifications and logical state, use IsOnline instead.
    /// </summary>
    [ObservableProperty]
    private bool _isVisuallyOnline = false;

    public string ToolTipString
    {
        get
        {
            // Handle wired mode display
            if (IsWiredMode)
            {
                string sourceText = DataSource == DataSource.GHub ? " (G)" : "(N)";
                return $"{sourceText} {DeviceName}, Wired Mode (charging)";
            }

            string statusText = !IsOnline ? "Offline" : $"{BatteryPercentage}%";
            string chargingText = PowerSupplyStatus == PowerSupplyStatus.CHARGING ? " (Charging)" : "";
            string sourceText2 = DataSource == DataSource.GHub ? " (G)" : "(N)";
#if DEBUG
            return $"{sourceText2} {DeviceName}{chargingText}, {statusText} - {LastUpdate}";
#else
            return $"{sourceText2} {DeviceName}, {statusText}{chargingText}";
#endif
        }
    }

    public Func<Task>? UpdateBatteryFunc;
    public async Task UpdateBatteryAsync()
    {
        if (UpdateBatteryFunc != null)
        {
            await UpdateBatteryFunc.Invoke();
        }
    }

    partial void OnLastUpdateChanged(DateTimeOffset value)
    {
        DiagnosticLogger.Log(ToolTipString);
    }

    public string GetXmlData()
    {
        return
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<xml>" +
            $"<device_id>{DeviceId}</device_id>" +
            $"<device_name>{DeviceName}</device_name>" +
            $"<device_type>{DeviceType}</device_type>" +
            $"<is_online>{IsOnline}</is_online>" +
            $"<battery_percent>{BatteryPercentage:f2}</battery_percent>" +
            $"<battery_voltage>{BatteryVoltage:f2}</battery_voltage>" +
            $"<mileage>{BatteryMileage:f2}</mileage>" +
            $"<charging>{PowerSupplyStatus == PowerSupplyStatus.CHARGING}</charging>" +
            $"<last_update>{LastUpdate}</last_update>" +
            $"</xml>"
            ;
    }
}
