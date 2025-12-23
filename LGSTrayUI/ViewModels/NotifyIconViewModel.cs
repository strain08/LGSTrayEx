using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LGSTrayCore;
using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayUI.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace LGSTrayUI;

public partial class NotifyIconViewModel : ObservableObject, IHostedService
{
    private readonly MainTaskbarIconWrapper _mainTaskbarIconWrapper;

    [ObservableProperty]
    private ObservableCollection<LogiDeviceViewModel> _logiDevices;

    [ObservableProperty]
    private ObservableCollection<LogiDeviceViewModel> _nativeDevices = new();

    [ObservableProperty]
    private ObservableCollection<LogiDeviceViewModel> _gHubDevices = new();

    [ObservableProperty]
    private bool _nativeMenuVisible = true;

    [ObservableProperty]
    private bool _gHubMenuVisible = true;

    private readonly UserSettingsWrapper _userSettings;
    private readonly AppSettings _appSettings;
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
                RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, true);
                _autoStart = registryKey?.GetValue(AutoStartRegKeyValue) != null;
            }

            return _autoStart ?? false;
        }
        set
        {
            RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, true);

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
        AppSettings appSettings
    )
    {
        _mainTaskbarIconWrapper = mainTaskbarIconWrapper;
        ((ContextMenu)Application.Current.FindResource("SysTrayMenu")).DataContext = this;

        _logiDevices = (logiDeviceCollection as LogiDeviceCollection)!.Devices;
        _userSettings = userSettings;
        _deviceManagers = deviceManagers;
        _appSettings = appSettings;

        // Set menu visibility from settings
        NativeMenuVisible = _appSettings.Native.Enabled;
        GHubMenuVisible = _appSettings.GHub.Enabled;

        // Subscribe to collection changes to update filtered views
        _logiDevices.CollectionChanged += LogiDevices_CollectionChanged;
        UpdateFilteredCollections();
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

        LogiDevice logiDevice = (LogiDevice)menuItem.DataContext;

        if (menuItem.IsChecked)
        {
            _userSettings.AddDevice(logiDevice.DeviceId);
        }
        else
        {
            _userSettings.RemoveDevice(logiDevice.DeviceId);
        }
    }

    [RelayCommand]
    private async Task RediscoverDevices()
    {
        DiagnosticLogger.Log("Rediscover devices");
        RediscoverDevicesEnabled = false;

        foreach (var manager in _deviceManagers)
        {
            manager.RediscoverDevices();
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

    private void LogiDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateFilteredCollections();
    }

    private void UpdateFilteredCollections()
    {
        // Update Native devices
        var nativeDevs = LogiDevices.Where(d => d.DataSource == DataSource.Native).ToList();
        NativeDevices.Clear();
        foreach (var dev in nativeDevs)
            NativeDevices.Add(dev);

        // Update GHub devices
        var ghubDevs = LogiDevices.Where(d => d.DataSource == DataSource.GHub).ToList();
        GHubDevices.Clear();
        foreach (var dev in ghubDevs)
            GHubDevices.Add(dev);
    }
}
