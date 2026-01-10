using CommunityToolkit.Mvvm.Messaging;
using LGSTrayCore;
using LGSTrayCore.Interfaces;
using LGSTrayCore.Managers;
using LGSTrayPrimitives;
using LGSTrayPrimitives.Interfaces;
using LGSTrayPrimitives.IPC;
using LGSTrayUI.Extensions;
using LGSTrayUI.IconDrawing;
using LGSTrayUI.Interfaces;
using LGSTrayUI.Messages;
using LGSTrayUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static LGSTrayUI.Extensions.AppExtensions;

namespace LGSTrayUI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;
    private bool _hasHandle = false; // Track ownership
    private IHost? _host; // DI container for accessing services
    private IEnumerable<IDeviceManager>? _deviceManagers; // Cached device managers for wake handler
    private PowerNotificationWindow? _powerWindow; // Hidden window for Modern Standby notifications
    private NotificationService? _notificationService; // Cached for power event handling
    private IMessenger? _messenger; // Cached messenger for power event handling

    /// <summary>
    /// Gets whether logging is enabled (--log flag).
    /// </summary>
    public static bool LoggingEnabled { get; private set; }

    /// <summary>
    /// Gets whether verbose logging is enabled (--verbose flag).
    /// </summary>
    public static bool VerboseLoggingEnabled { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "LGSTrayBattery_SingleInstance";

        // The 'true' here attempts to acquire ownership immediately
        _mutex = new Mutex(true, mutexName, out _hasHandle);

        if (!_hasHandle)
        {
            MessageBox.Show(
                "LGSTray is already running.",
                "LGSTray",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            Shutdown();
            return;
        }

        base.OnStartup(e);

        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CrashHandler);
        Microsoft.Win32.SystemEvents.PowerModeChanged += AppExtensions_PowerModeChanged;

        EnableEfficiencyMode();

        // STEP 1: Load configuration with validation service
        var validationService = new ConfigurationValidationService(); // Early instantiation before DI
        var builder = Host.CreateEmptyApplicationBuilder(null);
        if (!await LoadAppSettings(builder.Configuration, validationService))
        {
            Shutdown();
            return;
        }
        IConfiguration config = builder.Configuration;
        var appSettings = config.Get<AppSettings>()!;

        // STEP 2: Validate HID++ Software ID (must happen before daemon spawning)
        if (!validationService.ValidateAndEnforceSoftwareId(appSettings))
        {
            return; // Service already handled error display and shutdown
        }

        // STEP 3: Determine logging settings (config + CLI overrides)
        bool enableLogging = appSettings.Logging?.Enabled ?? false;
        bool enableVerbose = appSettings.Logging?.Verbose ?? false;

        // Parse command-line arguments for logging overrides
        if (e.Args.Contains("--log"))
        {
            enableLogging = true;
        }
        if (e.Args.Contains("--verbose"))
        {
            enableVerbose = true;
        }

        // Store in static properties for LGSTrayHIDManager
        LoggingEnabled = enableLogging;
        VerboseLoggingEnabled = enableVerbose;

#if DEBUG
        enableLogging = true;
        enableVerbose = false;
#endif

        // Initialize logging
        DiagnosticLogger.Initialize(enableLogging, enableVerbose);
        DiagnosticLogger.ResetLog();
        DiagnosticLogger.Log($"LGSTray {NotifyIconViewModel.AssemblyVersion} logging started.");
        if (enableVerbose)
        {
            DiagnosticLogger.Log("Verbose logging enabled.");
        }

        // Load Windows theme based on OS version
        LoadWindowsTheme();

        // DI setup
        builder.Services.Configure<AppSettings>(config);
        builder.Services.AddSingleton(appSettings);

        // Register WeakReferenceMessenger for intra-process messaging (power events, etc.)
        builder.Services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        // IPC, Settings
        builder.Services.AddLGSMessagePipe(true);
        builder.Services.AddSingleton<UserSettingsWrapper>();
        builder.Services.AddSingleton<ISettingsManager, TomlSettingsManager>();
        builder.Services.AddSingleton<IConfigurationValidationService, ConfigurationValidationService>();

        // Managers
        builder.Services.AddDeviceManager<LGSTrayHIDManager>(builder.Configuration);
        builder.Services.AddDeviceManager<GHubManager>(builder.Configuration);

        // UI
        builder.Services.AddSingleton<ILogiDeviceCollection, LogiDeviceCollection>();
        builder.Services.AddSingleton<ILogiDeviceIconFactory, LogiDeviceIconFactory>();
        builder.Services.AddSingleton<LogiDeviceViewModelFactory>();
        builder.Services.AddSingleton<IDispatcher, WpfDispatcher>();
        builder.Services.AddSingleton<MainTaskbarIconWrapper>();
        builder.Services.AddHostedService<NotifyIconViewModel>();

        // Notifiers
        builder.Services.AddWebSocketClientFactory();
        builder.Services.AddWebserver(builder.Configuration);
        builder.Services.AddMQTTClient(builder.Configuration);
        builder.Services.AddNotifications(builder.Configuration);

        var host = builder.Build();
        _host = host; // Store host reference for wake handler

        // Get messenger from DI
        var messenger = host.Services.GetRequiredService<IMessenger>();

        // Ensure ConfigurationValidationService is instantiated to listen for startup errors
        host.Services.GetRequiredService<IConfigurationValidationService>();

        // Create hidden window for Modern Standby power notifications
        // This works on both S3 (traditional sleep) and S0 (Modern Standby) systems
        _powerWindow = new PowerNotificationWindow(this, messenger);
        _powerWindow.Show(); // Must call Show() to initialize window handle
        _powerWindow.Hide(); // Then hide immediately

        await host.RunAsync();
        Dispatcher.InvokeShutdown();
    }

    private async void AppExtensions_PowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case Microsoft.Win32.PowerModes.Resume:
                DiagnosticLogger.Log("System resumed from sleep (SystemEvents.PowerModeChanged)");

                // Send message to resume notifications
                GetMessenger()?.Send(new SystemResumingMessage());

                await HandleSystemResumeAsync();
                break;
            case Microsoft.Win32.PowerModes.Suspend:
                DiagnosticLogger.Log("System is suspending to sleep (SystemEvents.PowerModeChanged)");

                // Send message to suspend notifications
                GetMessenger()?.Send(new SystemSuspendingMessage());
                break;
        }
    }

    /// <summary>
    /// Handle system wake - give USB devices time to stabilize, then rediscover.
    /// Called by both SystemEvents.PowerModeChanged (S3) and WM_POWERBROADCAST (Modern Standby).
    /// </summary>
    public async Task HandleSystemResumeAsync()
    {
        try
        {
            // Wait 2 seconds for USB devices to fully wake and stabilize
            // This delay allows HID devices to enumerate and become responsive
            await Task.Delay(2000);

            // Get device managers from DI container
            if (_deviceManagers == null)
            {
                var host = (Application.Current as App)?._host;
                if (host == null)
                {
                    DiagnosticLogger.LogWarning("Cannot access DI host for device manager rediscovery");
                    return;
                }
                _deviceManagers = host.Services.GetServices<IDeviceManager>();
            }

            // Trigger rediscovery on all active managers
            DiagnosticLogger.Log("Triggering rediscovery on all device managers");
            foreach (var manager in _deviceManagers)
            {
                _ = manager.RediscoverDevices();
                DiagnosticLogger.Log($"Rediscovery triggered for {manager.GetType().Name}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"Error during system resume handling: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the NotificationService instance from DI container (lazy cached).
    /// </summary>
    public NotificationService? GetNotificationService()
    {
        if (_notificationService == null && _host != null)
        {
            _notificationService = _host.Services.GetService<NotificationService>();
        }
        return _notificationService;
    }

    /// <summary>
    /// Gets the IMessenger instance from DI container (lazy cached).
    /// </summary>
    public IMessenger? GetMessenger()
    {
        if (_messenger == null && _host != null)
        {
            _messenger = _host.Services.GetRequiredService<IMessenger>();
        }
        return _messenger;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= AppExtensions_PowerModeChanged;
        // AppDomain.CurrentDomain.UnhandledException -= CrashHandler;

        // Close power notification window (cleans up WndProc hook and unregisters notifications)
        _powerWindow?.Close();
        _powerWindow = null;

        // ONLY release if we actually acquired it in OnStartup
        if (_hasHandle)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch
            {
                // Fail silently during exit if the handle is already gone
            }
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }

    static async Task<bool> LoadAppSettings(ConfigurationManager config, IConfigurationValidationService validationService)
    {
        return await validationService.LoadAndValidateConfiguration(config);
    }

    private void CrashHandler(object sender, UnhandledExceptionEventArgs args)
    {
        try
        {
            if (args.ExceptionObject is Exception ex)
            {
                string crashFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LGSTrayUI_Crash.txt");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[{DateTime.Now}] CRASH OCCURRED");

                // Recursively log exceptions
                Exception? currentEx = ex;
                int depth = 0;
                while (currentEx != null)
                {
                    string prefix = depth == 0 ? "Exception" : $"Inner Exception [{depth}]";
                    sb.AppendLine($"{prefix}: {currentEx.GetType().Name}: {currentEx.Message}");
                    sb.AppendLine($"Stack Trace:\n{currentEx.StackTrace}");

                    if (currentEx is AggregateException aggEx)
                    {
                        sb.AppendLine($"Flattened AggregateException Details:");
                        foreach (var inner in aggEx.Flatten().InnerExceptions)
                        {
                            sb.AppendLine($"- {inner.GetType().Name}: {inner.Message}");
                        }
                    }

                    currentEx = currentEx.InnerException;
                    depth++;
                    if (currentEx != null) sb.AppendLine();
                }

                sb.AppendLine("\nFull ToString():");
                sb.AppendLine(ex.ToString());
                sb.AppendLine("--------------------------------------------------");

                File.AppendAllText(crashFile, sb.ToString());
            }
        }
        catch
        {
            // If logging fails, there's not much we can do.
        }
    }

    /// <summary>
    /// Loads the appropriate WPF theme based on Windows version.
    /// Windows 11: Fluent theme for modern UI
    /// Windows 10: Classic WPF theme (no additional theme loaded)
    /// </summary>
    private void LoadWindowsTheme()
    {
        try
        {
            if (WindowsVersionHelper.IsWindows11OrGreater)
            {
                // Windows 11: Load Fluent theme
                var fluentTheme = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
                };
                Application.Current.Resources.MergedDictionaries.Add(fluentTheme);
                DiagnosticLogger.Log("Loaded Fluent theme for Windows 11");
            }
            else
            {
                // Windows 10: Use classic WPF theme (no action needed)
                DiagnosticLogger.Log("Using classic WPF theme for Windows 10 compatibility");
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: log error and continue with classic theme
            DiagnosticLogger.LogWarning($"Failed to load Fluent theme, using classic theme: {ex.Message}");
        }
    }
}

/// <summary>
/// Hidden window for receiving power management notifications.
/// Required for Modern Standby (S0) compatibility via RegisterSuspendResumeNotification.
/// Also works with traditional S3 sleep for backwards compatibility.
/// </summary>
internal class PowerNotificationWindow : Window
{
    private IntPtr _notificationHandle = IntPtr.Zero;
    private readonly App _app;
    private readonly IMessenger _messenger;

    // P/Invoke declarations for power management
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr hRecipient, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);

    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_APMSUSPEND = 0x0004;

    public PowerNotificationWindow(App app, IMessenger messenger)
    {
        _app = app;
        _messenger = messenger;

        // Create hidden window
        Width = 0;
        Height = 0;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Visibility = Visibility.Hidden;

        // Important: Don't call Show() - we just need the window to exist for message handling
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            // Get window handle and add WndProc hook
            var hwndHelper = new System.Windows.Interop.WindowInteropHelper(this);
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwndHelper.Handle);

            if (source != null)
            {
                source.AddHook(WndProc);

                // Register for suspend/resume notifications (required for Modern Standby S0)
                _notificationHandle = RegisterSuspendResumeNotification(hwndHelper.Handle, DEVICE_NOTIFY_WINDOW_HANDLE);

                if (_notificationHandle != IntPtr.Zero)
                {
                    DiagnosticLogger.Log("Power notification window registered successfully (Modern Standby + S3 compatible)");
                }
                else
                {
                    DiagnosticLogger.LogWarning("Failed to register for power notifications - wake detection may not work");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"Error initializing power notification window: {ex.Message}");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST)
        {
            int powerEvent = (int)wParam;

            if (powerEvent == PBT_APMSUSPEND)
            {
                DiagnosticLogger.Log("System suspending (WM_POWERBROADCAST)");

                // Send message to suspend notifications
                _messenger.Send(new SystemSuspendingMessage());
            }
            else if (powerEvent == PBT_APMRESUMEAUTOMATIC)
            {
                DiagnosticLogger.Log("System resumed (WM_POWERBROADCAST)");

                // Send message to resume notifications
                _messenger.Send(new SystemResumingMessage());

                // Call the app's resume handler asynchronously
                _ = _app.HandleSystemResumeAsync();
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Unregister power notifications
        if (_notificationHandle != IntPtr.Zero)
        {
            UnregisterSuspendResumeNotification(_notificationHandle);
            _notificationHandle = IntPtr.Zero;
            DiagnosticLogger.Log("Power notifications unregistered");
        }

        base.OnClosed(e);
    }
}