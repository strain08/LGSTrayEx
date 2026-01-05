using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayUI.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LGSTrayUI;

public partial class NotifyIconViewModel : ObservableObject, IHostedService
{
    private readonly MainTaskbarIconWrapper _mainTaskbarIconWrapper;

    [ObservableProperty]
    private ObservableCollection<LogiDeviceViewModel> _logiDevices;

    private readonly UserSettingsWrapper _userSettings;
    private readonly AppSettings _appSettings;
    private readonly ISettingsManager _settingsManager;

    public ICollectionView FilteredDevices { get; }

    public bool KeepOfflineDevices
    {
        get => _userSettings.KeepOfflineDevices;
        set
        {
            _userSettings.KeepOfflineDevices = value;
            OnPropertyChanged();
            FilteredDevices.Refresh();

            foreach (var device in LogiDevices)
            {
                device.UpdateIconVisibility();
            }
        }
    }

    public bool NumericDisplay
    {
        get
        {
            return _userSettings.NumericDisplay;
        }

        set
        {
            _userSettings.NumericDisplay = value;
            OnPropertyChanged();
        }
    }

    public static string AssemblyVersion
    {
        get
        {
            return "v" + Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "Missing";
        }
    }

    private const string AutoStartRegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartRegKeyValue = "LGSTrayGUI";
    private bool? _autoStart = null;
    public bool AutoStart
    {
        get
        {
            if (_autoStart == null)
            {
                using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, true);
                _autoStart = registryKey?.GetValue(AutoStartRegKeyValue) != null;
            }

            return _autoStart ?? false;
        }
        set
        {
            using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, true);

            if (registryKey == null)
            {
                return;
            }

            if (value)
            {
                registryKey.SetValue(AutoStartRegKeyValue, Path.Combine(AppContext.BaseDirectory, Environment.ProcessPath!));
            }
            else
            {
                registryKey.DeleteValue(AutoStartRegKeyValue, false);
            }

            _autoStart = value;
        }
    }

    [ObservableProperty]
    private bool _rediscoverDevicesEnabled = true;

    private readonly IEnumerable<IDeviceManager> _deviceManagers;

    public NotifyIconViewModel(
        MainTaskbarIconWrapper mainTaskbarIconWrapper,
        ILogiDeviceCollection logiDeviceCollection,
        UserSettingsWrapper userSettings,
        IEnumerable<IDeviceManager> deviceManagers,
        AppSettings appSettings,
        ISettingsManager settingsManager
    )
    {
        _mainTaskbarIconWrapper = mainTaskbarIconWrapper;
        ((ContextMenu)Application.Current.FindResource("SysTrayMenu")).DataContext = this;

        _logiDevices = (logiDeviceCollection as LogiDeviceCollection)!.Devices;
        _userSettings = userSettings;
        _deviceManagers = deviceManagers;
        _appSettings = appSettings;
        _settingsManager = settingsManager;

        FilteredDevices = CollectionViewSource.GetDefaultView(_logiDevices);
        
        // Enable live filtering so devices appear/disappear when BatteryPercentage changes
        if (FilteredDevices is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveFiltering)
        {
            liveShaping.LiveFilteringProperties.Add(nameof(LogiDeviceViewModel.BatteryPercentage));
            liveShaping.IsLiveFiltering = true;
        }

        FilteredDevices.Filter = FilterDevice;
    }

    private bool FilterDevice(object item)
    {
        if (KeepOfflineDevices)
        {
            return true;
        }

        if (item is LogiDeviceViewModel device)
        {
            // Assuming -1 indicates offline/unknown status as per LogiDevice.cs
            return device.BatteryPercentage >= 0; 
        }

        return true;
    }

    [RelayCommand]
    private static void ExitApplication()
    {
        Environment.Exit(0);
    }

    [RelayCommand]
    private void DeviceClicked(object? sender)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        LogiDeviceViewModel logiDevice = (LogiDeviceViewModel)menuItem.DataContext;

        if (menuItem.IsChecked)
        {
            _userSettings.AddSignature(logiDevice.DeviceSignature);
        }
        else
        {
            _userSettings.RemoveSignature(logiDevice.DeviceSignature);
        }
    }

    [RelayCommand]
    private async Task RediscoverDevices()
    {
        DiagnosticLogger.Log("NotifyIconViewModel: Rediscover devices");
        RediscoverDevicesEnabled = false;

        foreach (var manager in _deviceManagers)
        {
            _ = manager.RediscoverDevices();
        }

        await Task.Delay(10_000);

        RediscoverDevicesEnabled = true;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _mainTaskbarIconWrapper.Dispose();
        return Task.CompletedTask;
    }
}