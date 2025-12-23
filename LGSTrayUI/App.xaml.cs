using LGSTrayCore.Interfaces;
using LGSTrayCore.Managers;
using LGSTrayPrimitives;
using LGSTrayPrimitives.Interfaces;
using LGSTrayPrimitives.IPC;
using LGSTrayUI.Interfaces;
using LGSTrayUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notification.Wpf;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Tommy.Extensions.Configuration;
using static LGSTrayUI.AppExtensions;

namespace LGSTrayUI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;
    private bool _hasHandle = false; // Track ownership
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
        Microsoft.Win32.SystemEvents.PowerModeChanged += AppExtensions_PowerModeChanged
            ;
        EnableEfficiencyMode();

        // Parse command-line arguments for logging control
        bool enableLogging = e.Args.Contains("--log");
        bool enableVerbose = e.Args.Contains("--verbose");

        // Store in static properties for LGSTrayHIDManager
        LoggingEnabled = enableLogging;
        VerboseLoggingEnabled = enableVerbose;

#if DEBUG
        enableLogging = true;
        enableVerbose = true;
#endif

        // Initialize logging before any Log() calls
        DiagnosticLogger.Initialize(enableLogging, enableVerbose);

        DiagnosticLogger.ResetLog();
        DiagnosticLogger.Log("Logging started.");
        if (enableVerbose)
        {
            DiagnosticLogger.Log("Verbose logging enabled.");
        }

        var builder = Host.CreateEmptyApplicationBuilder(null);
        await LoadAppSettings(builder.Configuration);


        builder.Services.Configure<AppSettings>(builder.Configuration);

        // Register AppSettings as singleton for direct injection
        IConfiguration config = builder.Configuration;
        var appSettings = config.Get<AppSettings>()!;
        builder.Services.AddSingleton(appSettings);

        builder.Services.AddLGSMessagePipe(true);
        builder.Services.AddWebSocketClientFactory();
        builder.Services.AddSingleton<UserSettingsWrapper>();
        builder.Services.AddSingleton<INotificationManager, NotificationManager>();

        builder.Services.AddSingleton<ILogiDeviceIconFactory, LogiDeviceIconFactory>();
        builder.Services.AddSingleton<LogiDeviceViewModelFactory>();
        builder.Services.AddSingleton<IDispatcher, WpfDispatcher>();

        builder.Services.AddWebserver(builder.Configuration);

        builder.Services.AddIDeviceManager<LGSTrayHIDManager>(builder.Configuration);
        builder.Services.AddIDeviceManager<GHubManager>(builder.Configuration);
        builder.Services.AddSingleton<ILogiDeviceCollection, LogiDeviceCollection>();


        builder.Services.AddSingleton<MainTaskbarIconWrapper>();
        builder.Services.AddHostedService<NotifyIconViewModel>();
        builder.Services.AddHostedService<NotificationService>();

        var host = builder.Build();
        await host.RunAsync();
        Dispatcher.InvokeShutdown();
    }

    private void AppExtensions_PowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case Microsoft.Win32.PowerModes.Resume:
                DiagnosticLogger.Log("System resumed from sleep.");
                break;
            case Microsoft.Win32.PowerModes.Suspend:
                DiagnosticLogger.Log("System is suspending to sleep.");
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged-= AppExtensions_PowerModeChanged;

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

    static async Task LoadAppSettings(Microsoft.Extensions.Configuration.ConfigurationManager config)
    {
        try
        {
            config.AddTomlFile(Path.Combine(AppContext.BaseDirectory, "appsettings.toml"));
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException || ex is InvalidDataException)
            {
                var msgBoxRet = MessageBox.Show(
                    "Failed to read settings, do you want reset to default?",
                    "LGSTray - Settings Load Error",
                    MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No
                );

                if (msgBoxRet == MessageBoxResult.Yes)
                {
                    await File.WriteAllBytesAsync(
                        Path.Combine(AppContext.BaseDirectory, "appsettings.toml"),
                        LGSTrayUI.Properties.Resources.defaultAppsettings
                    );
                }

                config.AddTomlFile(Path.Combine(AppContext.BaseDirectory, "appsettings.toml"));
            }
            else
            {
                throw;
            }
        }
    }

    private void CrashHandler(object sender, UnhandledExceptionEventArgs args)
    {
        Exception e = (Exception)args.ExceptionObject;
        long unixTime = DateTimeOffset.Now.ToUnixTimeSeconds();

        using StreamWriter writer = new($"./crashlog_{unixTime}.log", false);
        writer.WriteLine(e.ToString());
    }
}