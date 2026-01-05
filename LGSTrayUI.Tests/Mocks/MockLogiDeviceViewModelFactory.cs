using LGSTrayPrimitives;
using LGSTrayUI.Services;
using System;

namespace LGSTrayUI.Tests.Mocks;

/// <summary>
/// Mock factory for creating LogiDeviceViewModel instances in tests
/// </summary>
public class MockLogiDeviceViewModelFactory
{
    private readonly LogiDeviceIconFactory _iconFactory;
    private readonly AppSettings _appSettings;
    private readonly UserSettingsWrapper _userSettings;

    public MockLogiDeviceViewModelFactory(LogiDeviceIconFactory iconFactory)
    {
        _iconFactory = iconFactory;
        _userSettings = new UserSettingsWrapper();
        _appSettings = new AppSettings
        {
            UI = new UISettings(),
            Backoff = new BackoffSettings(),
            GHub = new GHubManagerSettings(),
            HTTPServer = new HttpServerSettings(),
            Logging = new LoggingSettings(),
            Native = new NativeDeviceManagerSettings(),
            Notifications = new NotificationSettings()
        };
    }

    public LogiDeviceViewModel CreateViewModel(Action<LogiDeviceViewModel>? config = null)
    {
        var viewModel = new LogiDeviceViewModel(_iconFactory, _appSettings, _userSettings);
        config?.Invoke(viewModel);
        return viewModel;
    }
}