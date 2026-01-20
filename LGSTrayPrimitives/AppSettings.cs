namespace LGSTrayPrimitives;

public static class SettingsFile 
{
    public static readonly string Name = "appsettings.toml";
}

public class AppSettings
{
    public UISettings UI { get; set; } = null!;

    public HttpServerSettings HTTPServer { get; set; } = null!;

    public GHubManagerSettings GHub { get; set; } = null!;

    public NativeDeviceManagerSettings Native { get; set; } = null!;

    public NotificationSettings Notifications { get; set; } = null!;

    public LoggingSettings Logging { get; set; } = null!;

    public BackoffSettings Backoff { get; set; } = new();

    public MQTTSettings MQTT { get; set; } = null!;
}

public class UISettings
{
    public bool EnableRichToolTips { get; set; }
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

public class GHubManagerSettings
{
    public bool Enabled { get; set; }
}

public class NativeDeviceManagerSettings : GHubManagerSettings
{
    public int RetryTime { get; set; } = 10;
    public int PollPeriod { get; set; } = 600;

    /// <summary>
    /// HID++ protocol software identifier (1-15, default: 10).
    /// Used to distinguish responses from different applications accessing the same device.
    /// CRITICAL for multi-user/multi-instance scenarios - each instance MUST use a unique value.
    /// Valid range: 1-15 (0 is reserved for device events).
    /// Invalid values are automatically clamped to valid range.
    /// </summary>
    public int SoftwareId { get; set; } = 10;

    /// <summary>
    /// Minimum valid software ID (HID++ 2.0 spec: 0x00 reserved for device events)
    /// </summary>
    public const int MinValidSoftwareId = 1;

    /// <summary>
    /// Maximum valid software ID (HID++ 2.0 spec: 4-bit field, 0x0F max)
    /// </summary>
    public const int MaxValidSoftwareId = 15;

    /// <summary>
    /// Validates the configured software ID.
    /// </summary>
    /// <returns>True if valid (1-15), false otherwise</returns>
    public bool IsSoftwareIdValid() => SoftwareId >= MinValidSoftwareId && SoftwareId <= MaxValidSoftwareId;

    /// <summary>
    /// Gets a user-friendly error message for invalid software ID.
    /// </summary>
    public string GetSoftwareIdErrorMessage()
    {
        return $"Invalid HID++ Software ID: {SoftwareId}\n\n" +
               $"Valid softwareId is between {MinValidSoftwareId} and {MaxValidSoftwareId}.\n" +
               $"Please change appsettings.toml softwareId setting and restart.";
    }

    /// <summary>
    /// Enable battery event-driven updates (default: true).
    /// When enabled, devices send unsolicited battery updates on state changes.
    /// Polling continues to run in parallel for validation and fallback.
    /// </summary>
    public bool EnableBatteryEvents { get; set; } = true;

    /// <summary>
    /// Keep battery polling active even when event-driven updates are received (default: false).
    /// When false, polling stops after first battery event (current behavior).
    /// When true, polling continues as fallback for validation and devices that stop sending events.
    /// Deduplication prevents redundant IPC messages when both sources report same state.
    /// </summary>
    public bool KeepPollingWithEvents { get; set; } = false;

    /// <summary>
    /// Delay in seconds before processing battery EVENT data after device ON (default: 0).
    /// Prevents spurious battery readings during device wake/initialization.
    /// Battery values from events during delay window are ignored (not published to UI).
    /// Battery POLLS are not delayed (always processed).
    /// Set to 0 to disable delay (process all events immediately).
    /// Recommended: 5 seconds for devices with unstable wake behavior.
    /// </summary>
    public int BatteryEventDelayAfterOn { get; set; } = 0;

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

    /// <summary>
    /// Suppress offline/online notification spam when devices switch between wired/wireless modes (default: true).
    /// When enabled, offline notifications are delayed by ModeSwitchDetectionDelaySeconds.
    /// If device comes back online within that window, both notifications are suppressed.
    /// </summary>
    public bool SuppressModeSwitchNotifications { get; set; } = true;

    /// <summary>
    /// Delay in seconds before showing offline notification (default: 3).
    /// If device reconnects within this window, notifications are suppressed (mode switch detected).
    /// Only applies when SuppressModeSwitchNotifications is enabled.
    /// Recommended: 2-5 seconds for most devices.
    /// </summary>
    public int ModeSwitchDetectionDelaySeconds { get; set; } = 3;

    /// <summary>
    /// List of device names to apply mode switch suppression (default: empty = all devices).
    /// If empty, suppression applies to all devices.
    /// If specified, only listed devices will have suppressed notifications during mode switches.
    /// Example: ["G502", "G915"]
    /// </summary>
    public IEnumerable<string> DevicesForModeSwitchSuppression { get; set; } = [];
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

public class BackoffSettings
{
    public BackoffProfile Init { get; set; } = BackoffProfile.DefaultInit;
    public BackoffProfile Battery { get; set; } = BackoffProfile.DefaultBattery;
    public BackoffProfile Metadata { get; set; } = BackoffProfile.DefaultMetadata;
    public BackoffProfile FeatureEnum { get; set; } = BackoffProfile.DefaultFeatureEnum;
    public BackoffProfile Ping { get; set; } = BackoffProfile.DefaultPing;
    public BackoffProfile ReceiverInit { get; set; } = BackoffProfile.DefaultReceiverInit;
}

public class BackoffProfile
{
    public required string Name { get; init; }
    public int InitialDelayMs { get; set; }
    public int MaxDelayMs { get; set; }
    public int InitialTimeoutMs { get; set; }
    public int MaxTimeoutMs { get; set; }
    public double Multiplier { get; set; } = 2.0;
    public int MaxAttempts { get; set; }

    /// <summary>
    /// Converts this profile to a BackoffStrategy instance.
    /// Auto-corrects invalid configurations and logs warnings.
    /// </summary>
    public Retry.BackoffStrategy ToStrategy()
    {
        // Validate and auto-correct configuration
        int correctedMaxDelayMs = MaxDelayMs;
        int correctedMaxTimeoutMs = MaxTimeoutMs;
        bool hadErrors = false;

        // Ensure maxDelay >= initialDelay
        if (MaxDelayMs < InitialDelayMs)
        {
            correctedMaxDelayMs = InitialDelayMs;
            DiagnosticLogger.LogWarning($"[BackoffProfile] Invalid config: maxDelayMs ({MaxDelayMs}) < initialDelayMs ({InitialDelayMs}). Auto-corrected to {correctedMaxDelayMs}ms.");
            hadErrors = true;
        }

        // Ensure maxTimeout >= initialTimeout
        if (MaxTimeoutMs < InitialTimeoutMs)
        {
            correctedMaxTimeoutMs = InitialTimeoutMs;
            DiagnosticLogger.LogWarning($"[BackoffProfile] Invalid config: maxTimeoutMs ({MaxTimeoutMs}) < initialTimeoutMs ({InitialTimeoutMs}). Auto-corrected to {correctedMaxTimeoutMs}ms.");
            hadErrors = true;
        }

        // Ensure multiplier > 1.0
        double correctedMultiplier = Multiplier;
        if (Multiplier <= 1.0)
        {
            correctedMultiplier = 2.0;
            DiagnosticLogger.LogWarning($"[BackoffProfile] Invalid config: multiplier ({Multiplier}) <= 1.0. Auto-corrected to {correctedMultiplier}.");
            hadErrors = true;
        }

        // Ensure maxAttempts >= 1
        int correctedMaxAttempts = MaxAttempts;
        if (MaxAttempts < 1)
        {
            correctedMaxAttempts = 1;
            DiagnosticLogger.LogWarning($"[BackoffProfile] Invalid config: maxAttempts ({MaxAttempts}) < 1. Auto-corrected to {correctedMaxAttempts}.");
            hadErrors = true;
        }

        if (hadErrors)
        {
            DiagnosticLogger.LogWarning("[BackoffProfile] Configuration errors detected and auto-corrected. Please review appsettings.toml [Backoff.*] sections.");
        }

        return new Retry.BackoffStrategy(
            initialDelay: TimeSpan.FromMilliseconds(InitialDelayMs),
            maxDelay: TimeSpan.FromMilliseconds(correctedMaxDelayMs),
            initialTimeout: TimeSpan.FromMilliseconds(InitialTimeoutMs),
            maxTimeout: TimeSpan.FromMilliseconds(correctedMaxTimeoutMs),
            multiplier: correctedMultiplier
        )
        {
            ProfileName = Name
        };
    }

    /// <summary>
    /// Default profile for device initialization.
    /// Progressive backoff with 60s max delay cap.
    /// </summary>
    public static BackoffProfile DefaultInit => new()
    {
        Name = "Init",
        InitialDelayMs = 2000,      // 2s
        MaxDelayMs = 60000,         // 60s cap
        InitialTimeoutMs = 1000,    // 1s
        MaxTimeoutMs = 5000,        // 5s
        Multiplier = 2.0,
        MaxAttempts = 10
    };

    /// <summary>
    /// Default profile for battery query operations.
    /// Quick retry with lower delay cap.
    /// </summary>
    public static BackoffProfile DefaultBattery => new()
    {
        Name= "Battery",
        InitialDelayMs = 0,         // No delay on first retry
        MaxDelayMs = 10000,         // 10s cap
        InitialTimeoutMs = 1000,    // 1s
        MaxTimeoutMs = 5000,        // 5s
        Multiplier = 2.0,
        MaxAttempts = 3             // Quick retry
    };

    /// <summary>
    /// Default profile for metadata retrieval operations.
    /// Moderate backoff for sleeping devices.
    /// </summary>
    public static BackoffProfile DefaultMetadata => new()
    {
        Name= "Metadata",
        InitialDelayMs = 500,       // 500ms
        MaxDelayMs = 30000,         // 30s cap
        InitialTimeoutMs = 500,     // 500ms
        MaxTimeoutMs = 3000,        // 3s
        Multiplier = 2.0,
        MaxAttempts = 5
    };

    /// <summary>
    /// Default profile for feature enumeration.
    /// Moderate retry for slow devices.
    /// </summary>
    public static BackoffProfile DefaultFeatureEnum => new()
    {
        Name= "FeatureEnum",
        InitialDelayMs = 1000,      // 1s
        MaxDelayMs = 30000,         // 30s cap
        InitialTimeoutMs = 1000,    // 1s
        MaxTimeoutMs = 5000,        // 5s
        Multiplier = 2.0,
        MaxAttempts = 3
    };

    /// <summary>
    /// Default profile for ping operations.
    /// Fast retry with short timeouts.
    /// </summary>
    public static BackoffProfile DefaultPing => new()
    {
        Name= "Ping",
        InitialDelayMs = 100,       // 100ms
        MaxDelayMs = 5000,          // 5s cap
        InitialTimeoutMs = 100,     // 100ms
        MaxTimeoutMs = 1000,        // 1s
        Multiplier = 2.0,
        MaxAttempts = 5
    };

    /// <summary>
    /// Default profile for HID++ 1.0 receiver operations.
    /// Quick retry for initialization commands (QueryDeviceCount, EnableAllReports).
    /// </summary>
    public static BackoffProfile DefaultReceiverInit => new()
    {
        Name = "ReceiverInit",
        InitialDelayMs = 500,       // 500ms - delay before second attempt
        MaxDelayMs = 5000,          // 5s cap - keep total time bounded
        InitialTimeoutMs = 1000,    // 1s - standard HID++ 1.0 timeout
        MaxTimeoutMs = 3000,        // 3s - allow more time on retries
        Multiplier = 2.0,           // Standard exponential backoff
        MaxAttempts = 3             // Quick retry (3 attempts total: ~9s max)
    };
}

public class MQTTSettings
{
    /// <summary>
    /// Enable MQTT publishing for Home Assistant integration
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// MQTT broker address (hostname or IP)
    /// </summary>
    public string BrokerAddress { get; set; } = "localhost";

    /// <summary>
    /// MQTT broker port (default: 1883 for non-TLS, 8883 for TLS)
    /// </summary>
    public int Port { get; set; } = 1883;

    /// <summary>
    /// MQTT username (leave empty for anonymous)
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// MQTT password (leave empty for anonymous)
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Topic prefix for Home Assistant discovery (default: homeassistant)
    /// </summary>
    public string TopicPrefix { get; set; } = "homeassistant";

    /// <summary>
    /// Minimum interval between publishes for the same device (seconds)
    /// Prevents message spam when battery updates rapidly
    /// </summary>
    public int PublishThrottleSeconds { get; set; } = 60;

    /// <summary>
    /// Enable TLS/SSL for broker connection
    /// </summary>
    public bool UseTLS { get; set; } = false;

    /// <summary>
    /// Show toast notifications for MQTT connection status changes
    /// </summary>
    public bool NotifyConnectionStatus { get; set; } = true;

    /// <summary>
    /// Client ID for MQTT connection (default: LGSTrayBattery_{hostname})
    /// Leave empty to auto-generate
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Retain messages on MQTT broker (recommended for Home Assistant)
    /// </summary>
    public bool RetainMessages { get; set; } = true;
}
