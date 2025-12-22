using LGSTrayCore.HttpServer;
using LGSTrayCore.Managers;
using LGSTrayCore.WebSocket;
using LGSTrayPrimitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static LGSTrayPrimitives.DiagnosticLogger;

namespace LGSTrayCore.Interfaces;

public static class IServiceExtension
{
    public static void AddWebserver(this IServiceCollection services, IConfiguration configs)
    {
        var settings = configs.Get<AppSettings>()!;
        if (!settings.HTTPServer.Enabled) return;

        services.AddSingleton<HttpControllerFactory>();
        services.AddHostedService<HttpServer.HttpServer>();
    }

    public static IServiceCollection AddWebSocketClientFactory(this IServiceCollection services)
    {
        services.AddSingleton<IWebSocketClientFactory, WebSocketClientFactory>();
        return services;
    }

    public static void AddIDeviceManager<T>(this IServiceCollection services, IConfiguration configs) where T : class, IDeviceManager, IHostedService
    {
        var settings = configs.Get<AppSettings>()!;
        string managerName = typeof(T).Name;
        bool isEnabled = typeof(T) switch
        {
            { } when typeof(T) == typeof(GHubManager) => settings.GHub.Enabled,
            { } when typeof(T) == typeof(LGSTrayHIDManager) => settings.Native.Enabled,
            _ => false
        };

        if (!isEnabled)
        {
            DiagnosticLogger.Log($"{managerName} disabled in config");
            return;
        }

        DiagnosticLogger.Log($"Starting {managerName}");
        services.AddSingleton<T>();
        services.AddSingleton<IDeviceManager>(p => p.GetRequiredService<T>());
        services.AddSingleton<IHostedService>(p => p.GetRequiredService<T>());
    }
}
