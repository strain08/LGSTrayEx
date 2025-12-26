namespace LGSTrayPrimitives;

public class AppSettings
{
    public UISettings UI { get; set; } = null!;

    public HttpServerSettings HTTPServer { get; set; } = null!;

    public IDeviceManagerSettings GHub { get; set; } = null!;

    public NativeDeviceManagerSettings Native { get; set; } = null!;

    public NotificationSettings Notifications { get; set; } = null!;

    public LoggingSettings Logging { get; set; } = null!;
}

public class UISettings
{
    public bool EnableRichToolTips { get; set; }

    /// <summary>
    /// Keep offline devices visible in tray menu (marked with BatteryPercentage = -1).
    /// When false (default), devices are removed from menu when disconnected.
    /// </summary>
    public bool KeepOfflineDevices { get; set; } = false;
}

public class HttpServerSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; }

    private string _addr = null!;
    public string Addr
    {
        get => _addr;
        set => _addr = (value == "0.0.0.0") ? "+" : value;
    }

    public bool UseIpv6 { get; set; }

    public string UrlPrefix => $"http://{Addr}:{Port}";
}

public class IDeviceManagerSettings
{
    public bool Enabled { get; set; }
}

public class NativeDeviceManagerSettings : IDeviceManagerSettings
{
    public int RetryTime { get; set; } = 10;
    public int PollPeriod { get; set; } = 600;

    /// <summary>
    /// Enable battery event-driven updates (default: true).
    /// When enabled, devices send unsolicited battery updates on state changes.
    /// Polling continues to run in parallel for validation and fallback.
    /// </summary>
    public bool EnableBatteryEvents { get; set; } = true;

    public IEnumerable<string> DisabledDevices { get; set; } = [];
}

public class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool NotifyStateChange { get; set; } = true;
    public bool NotifyOnBatteryLow { get; set; } = true;
    public int BatteryLowThreshold { get; set; } = 30;
    public bool NotifyOnBatteryHigh { get; set; } = true;
    public int BatteryHighThreshold { get; set; } = 80;
}

public class LoggingSettings
{
    /// <summary>
    /// Enable diagnostic logging to diagnostic.log file.
    /// Can be overridden by --log command-line flag.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Enable verbose diagnostic logging (includes detailed trace messages).
    /// Requires Enabled = true or --log flag.
    /// Can be overridden by --verbose command-line flag.
    /// </summary>
    public bool Verbose { get; set; } = false;
}
