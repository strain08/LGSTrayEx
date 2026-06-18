using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LGSTrayCore;
using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayPrimitives.Messages;
using LGSTrayUI.IconDrawing;
using LGSTrayUI.Interfaces;
using LGSTrayUI.Messages;
using LGSTrayUI.Services;
using MessagePipe;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

    public ICollectionView FilteredDevices { get; init; }

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
        get => _userSettings.NumericDisplay;
        set
        {
            _userSettings.NumericDisplay = value;
            OnPropertyChanged();
        }
    }

    public static string AssemblyVersion => "v" + Assembly.GetEntryAssembly()?
                                                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                                                          .InformationalVersion?
                                                          .Split('+')[0] + "-strain08" ?? "Missing";

    public static string AssemblyBuildInfo => BuildInfo.FromAssembly(Assembly.GetEntryAssembly()!);

    /// <summary>
    /// Whether diagnostic logging is enabled. Controls visibility of the "Open Log Folder" menu item.
    /// </summary>
    public static bool LoggingEnabled => App.LoggingEnabled;


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
    private readonly ISubscriber<DeviceBatteryUpdatedMessage> _batterySubscriber;
    private IDisposable? _subscriptions;

    public NotifyIconViewModel(MainTaskbarIconWrapper mainTaskbarIconWrapper,
                               ILogiDeviceCollection logiDeviceCollection,
                               UserSettingsWrapper userSettings,
                               IEnumerable<IDeviceManager> deviceManagers,
                               ILogiDeviceIconFactory logiDeviceIconFactory,
                               ISubscriber<DeviceBatteryUpdatedMessage> batterySubscriber)
    {
        _mainTaskbarIconWrapper = mainTaskbarIconWrapper;
        _batterySubscriber = batterySubscriber;

        // Set this ViewModel as the DataContext for all ContextMenus
        // Each icon now gets its own ContextMenu instance to prevent stuck menu states
        _mainTaskbarIconWrapper.SetContextMenuDataContext(this);
        logiDeviceIconFactory.SetContextMenuDataContext(this);

        _logiDevices = (logiDeviceCollection as LogiDeviceCollection)!.Devices;
        _userSettings = userSettings;
        _deviceManagers = deviceManagers;

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
        DiagnosticLogger.Log("Exit application requested");
        Application.Current.Shutdown();
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
    private static void OpenLogFolder()
    {
        var logPath = DiagnosticLogger.LogFilePath;
        try
        {
            if (File.Exists(logPath))
            {                
                Process.Start("explorer.exe", $"/select,\"{logPath}\"");
            }
            else
            {             
                Process.Start("explorer.exe", $"\"{AppDataPaths.LocalAppDataDir}\"");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"Failed to open log folder: {ex.Message}");
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
        // Show a device that registers while the context menu is already open
        _subscriptions = _batterySubscriber.Subscribe(OnDeviceBatteryUpdated);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriptions?.Dispose();
        _mainTaskbarIconWrapper.Dispose();
        return Task.CompletedTask;
    }

    private void OnDeviceBatteryUpdated(DeviceBatteryUpdatedMessage message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => RefreshIfNewlyVisible(message.Device));
        }
        else
        {
            RefreshIfNewlyVisible(message.Device);
        }
    }

    private void RefreshIfNewlyVisible(LogiDevice device)
    {
        // Refresh only on the hidden->visible transition; skip steady-state updates to avoid
        // Reset churn that would flicker an open menu.
        if (FilterDevice(device) && !FilteredDevices.Contains(device))
        {
            FilteredDevices.Refresh();
        }
    }
}