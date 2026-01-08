using LGSTrayPrimitives;
using LGSTrayUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Notification.Wpf;

namespace LGSTrayUI.Extensions;

internal static class ServiceCollectionExtensions {
    public static void AddMQTTClient(this IServiceCollection services, IConfiguration configs) {
        var settings = configs.Get<AppSettings>() ?? throw new System.Exception("Settings null in ServiceCollectionExtensions !");

#if DEBUG
        settings.MQTT.Enabled = true;        
#endif

        if (settings.MQTT.Enabled) {
            services.AddHostedService<MQTTService>();
            DiagnosticLogger.Log("MQTT service enabled");
        }
    }
    public static void AddNotifications(this IServiceCollection services, IConfiguration configs) {
        var settings = configs.Get<AppSettings>() ?? throw new System.Exception("Settings null in ServiceCollectionExtensions !");

        if (settings.Notifications.Enabled) {
            services.AddSingleton<INotificationManager, NotificationManager>();
            services.AddHostedService<NotificationService>();
            DiagnosticLogger.Log("Notification service enabled");
        }
    }
}