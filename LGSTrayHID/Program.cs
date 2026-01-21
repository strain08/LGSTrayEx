using LGSTrayHID.Protocol;
using LGSTrayPrimitives;
using LGSTrayPrimitives.IPC;
using LGSTrayPrimitives.Retry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using Tommy.Extensions.Configuration;

namespace LGSTrayHID;

internal static class GlobalSettings
{
    public static NativeDeviceManagerSettings settings = new();

    /// <summary>
    /// Validated HID++ software ID (1-15).
    /// Set during application startup after validation.
    /// </summary>
    public static byte SoftwareId { get; set; } = HidppSoftwareId.DEFAULT;

    // Backoff strategies for retry operations
    public static BackoffStrategy InitBackoff { get; set; } = BackoffProfile.DefaultInit.ToStrategy();
    public static BackoffStrategy BatteryBackoff { get; set; } = BackoffProfile.DefaultBattery.ToStrategy();
    public static BackoffStrategy MetadataBackoff { get; set; } = BackoffProfile.DefaultMetadata.ToStrategy();
    public static BackoffStrategy FeatureEnumBackoff { get; set; } = BackoffProfile.DefaultFeatureEnum.ToStrategy();
    public static BackoffStrategy PingBackoff { get; set; } = BackoffProfile.DefaultPing.ToStrategy();
    public static BackoffStrategy ReceiverInitBackoff { get; set; } = BackoffProfile.DefaultReceiverInit.ToStrategy();
}

internal class Program
{
    static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionTrapper;

        var builder = Host.CreateEmptyApplicationBuilder(null);

        // Load Logging config
        builder.Configuration.AddTomlFile(SettingsFile.Name, optional: false, reloadOnChange: false);
        builder.Configuration.AddTomlFile(SettingsFile.LocalName, optional: true, reloadOnChange: false);
        var loggingSettings = builder.Configuration.GetSection("Logging").Get<LoggingSettings>();

        // Determine logging settings
        bool enableLogging = loggingSettings?.Enabled ?? false;
        bool enableVerbose = loggingSettings?.Verbose ?? false;

        // Command-line overrides
        if (args.Contains("--log")) enableLogging = true;
        if (args.Contains("--verbose")) enableVerbose = true;

        // Initialize logging
        DiagnosticLogger.Initialize(enableLogging, enableVerbose);

        GlobalSettings.settings = builder.Configuration.GetSection("Native")
            .Get<NativeDeviceManagerSettings>() ?? GlobalSettings.settings;

        // Validate and set software ID
        try
        {
            GlobalSettings.SoftwareId = HidppSoftwareId.ValidateAndConvert(GlobalSettings.settings.SoftwareId);
            DiagnosticLogger.Log($"Using HID++ software ID: {GlobalSettings.SoftwareId} (0x{GlobalSettings.SoftwareId:X2})");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            string errorMessage = $"FATAL: Invalid HID++ software ID configuration.\n\n{ex.Message}\n\nApplication will now exit.";
            DiagnosticLogger.LogError(errorMessage);
            Console.Error.WriteLine(errorMessage);
            Environment.Exit(1);
            return; // Unreachable, but satisfies compiler
        }

        // Load backoff settings
        var backoffSettings = builder.Configuration.GetSection("Backoff").Get<BackoffSettings>() ?? new BackoffSettings();
        GlobalSettings.InitBackoff = backoffSettings.Init.ToStrategy();
        GlobalSettings.BatteryBackoff = backoffSettings.Battery.ToStrategy();
        GlobalSettings.MetadataBackoff = backoffSettings.Metadata.ToStrategy();
        GlobalSettings.FeatureEnumBackoff = backoffSettings.FeatureEnum.ToStrategy();
        GlobalSettings.PingBackoff = backoffSettings.Ping.ToStrategy();
        GlobalSettings.ReceiverInitBackoff = backoffSettings.ReceiverInit.ToStrategy();

        builder.Services.AddLGSMessagePipe();
        builder.Services.AddHostedService<HidppManagerService>();

        var host = builder.Build();

            _ = Task.Run(async () =>
            {
                bool ret = int.TryParse(args.ElementAtOrDefault(0), out int parentPid);
                if (!ret)
                {
#if DEBUG
                    return;
#else
                // Started without a parent, assume invalid.
                Environment.Exit(0);
#endif
                }

                await Process.GetProcessById(parentPid).WaitForExitAsync();

                CancellationTokenSource cts = new(5000);
                await host.StopAsync(cts.Token);

                Environment.Exit(0);
            });

            await host.RunAsync();
    }

    private static void UnhandledExceptionTrapper(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception ex)
            {
                var unixTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                string crashFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"LGSTrayHID_Crash_{unixTime}.txt");
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
}