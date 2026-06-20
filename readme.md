# LGSTrayEx

**Note:** This is a community-maintained fork of the original
[andyvorld/LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery).<br>
The original project appears unmaintained since February 2024. <br>
This fork continues development with bug fixes and new features.
#### Check the original page for detailed documentation.

## Install
[![GitHub Release](https://img.shields.io/github/v/release/strain08/LGSTrayEx?sort=semver)](https://github.com/strain08/LGSTrayBattery/releases/latest)

## Configuration
See included [settings file](https://github.com/strain08/LGSTrayBattery/blob/master/LGSTrayUI/appsettings.toml) for documentation.

## New Features
- MQTT client, advertise to HA
- Notifications for device on/off, battery level thresholds, configurable
- Improved numeric display with small battery bar
- Configurable colors for battery level bar in icon mode, per light/dark theme
- Support for monitoring devices that connect both wired and wireless
- Ghub and Native devices: choose which source you want to monitor from devices menu
- Menu option Keep Offline Devices: configures tray icon behaviour
- Device On/Off status represented in http xml with is_online key
- Dark / Light menu

See [release notes](https://github.com/strain08/LGSTrayBattery/releases/latest)

## Device compatibility
- 🔋 Wireless mice/keyboards/headsets using Unifying, Bolt, Lightspeed or direct Bluetooth receivers
- 🔋 Centurion protocol devices - G522, PRO X 2, etc. - reverse-engineered and c#'ed by [@ksanislo](https://github.com/ksanislo)
- 🔋 Devices from roughly 2010+ that support HID++ 2.0
- 🪫 Older HID++ 1.0 devices are likely unsupported (ping will fail)

Currently tested with:
- ✅(Bolt) MX Keys S
- ✅(Bolt) MX Anywhere 3S
- ✅(Bolt) MX Master 3
- ✅(Bolt) MX Mechanical
- ✅(Unifying) M330 Silent
- ✅(Unifying) MX Vertical
- ✅(Lightspeed) G Pro X 2 Mouse
- ✅(Lightspeed) G305 Mouse
- ✅(Lightspeed) G705 Mouse
- ✅(Lightspeed) G515 TKL
- ✅(Lightspeed) G522 Headset Native (Centurion) / Ghub
- ✅(Lightspeed) G733 Headset Native / Ghub

## OS Compatibility
- currently Windows 11 only

## Known issues
- `enableRichToolsips = true` coud result in tooltips not closing properly when using fullscreen apps<br>
  Set this to `false` for classic windows tooltips. There is no specific functionality implemented for this.<br>
  It will default to `false` in versions after v3.1.3
- if application does not start / does not produce log file please ensure proper permissions in install folder:
  Current user and SYSTEM must have Full Control over folder and files.

## Issue reporting
1. Edit `appsettings.toml`
```
[Logging]
enabled = true
verbose = true
maxLines = 0
```
2. Reproduce issue
3. Post the resulting `%LOCALAPPDATA%\LGSTray\diagnostic.log` along with the issue description and repro.
4. !! Make sure to disable logging or limit size using maxLines = 1000 when finished

## Building from source

### Prerequisites
- Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A prebuilt `hidapi.dll` is already checked in under `LGSTrayHID/HidApi/libhidapi/`, so rebuilding it is only needed if you want to update it (see below)
- Included hidapi.dll is same as [andy's](https://github.com/andyvorld/LGSTrayBattery/tree/master/LGSTrayHID/libhidapi), SHA256: `38bda32f593c054cacaf95bebce36f9bacc7fbd0740f7b6f72f6d368fbc84b4d`

### LGSTray (C#)
From the repo root:
```powershell
dotnet build LGSTrayBattery.sln              # Build entire solution
dotnet build LGSTrayBattery.sln -c Release   # Release build
dotnet run --project LGSTrayUI/LGSTrayUI.csproj   # Run the UI (Debug shows a console)
```

To produce release packages (`Standalone` and `Framedep` zips for `LGSTray.exe`/`LGSTrayHID.exe`):
```powershell
python publish.py
```

### libhidapi (native dependency)
LGSTrayHID talks to devices through `hidapi.dll`, built from [libusb/hidapi](https://github.com/libusb/hidapi) via CMake. Requires CMake and a C/C++ toolchain (e.g. Visual Studio Build Tools) on `PATH`, plus git for CMake's `FetchContent` to fetch the hidapi source.

From `LGSTrayHID/HidApi/libhidapi/`:
```powershell
cmake -B build
cmake --build .\build --target hidapi_winapi --config Release
```
or simply run `hidapi_build.bat` in that folder. The resulting `hidapi.dll` is written back into `LGSTrayHID/HidApi/libhidapi/`; copy it over the checked-in copy to update it.
