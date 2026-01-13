using LGSTrayPrimitives;
using LGSTrayUI.Interfaces;
using LGSTrayUI.Services;
using System;

namespace LGSTrayUI;

public class LogiDeviceViewModelFactory
{
    private readonly ILogiDeviceIconFactory _logiDeviceIconFactory;
    private readonly AppSettings _appSettings;
    private readonly UserSettingsWrapper _userSettings;

    public LogiDeviceViewModelFactory(ILogiDeviceIconFactory logiDeviceIconFactory, AppSettings appSettings, UserSettingsWrapper userSettings)
    {
        _logiDeviceIconFactory = logiDeviceIconFactory;
        _appSettings = appSettings;
        _userSettings = userSettings;
    }

    public LogiDeviceViewModel CreateViewModel(Action<LogiDeviceViewModel>? config = null)
    {
        LogiDeviceViewModel output = new(_logiDeviceIconFactory, _appSettings, _userSettings);
        config?.Invoke(output);

        return output;
    }
}
