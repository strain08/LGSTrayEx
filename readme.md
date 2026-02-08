# LGSTrayEx

**Note:** This is a community-maintained fork of the original 
[andyvorld/LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery).<br>
The original project appears unmaintained since February 2024. <br>
This fork continues development with bug fixes and new features.
#### Check the original page for detailed documentation.

## Install
[![GitHub Release](https://img.shields.io/github/v/release/strain08/LGSTrayBattery?sort=semver)](https://github.com/strain08/LGSTrayBattery/releases/latest)

## Configuration
See included [settings file](https://github.com/strain08/LGSTrayBattery/blob/master/LGSTrayUI/appsettings.toml) for documentation.

## New Features
- MQTT client, advertise to HA
- Notifications for device on/off, battery level thresholds, configurable
- Improved numeric display with small battery bar
- Support for monitoring devices that connect both wired and wireless
- Ghub and Native devices: choose which source you want to monitor from devices menu
- Menu option Keep Offline Devices: configures tray icon behaviour
- Device On/Off status represented in http xml with is_online key
- Dark / Light menu

See [release notes](https://github.com/strain08/LGSTrayBattery/releases/latest)

## Device compatibility
Currently tested and working with:
- ✅(Bolt) MX Keys S
- ✅(Bolt) MX Anywhere 3S
- ✅(Bolt) MX Master 3
- ✅(Unifying) M330 Silent
- ✅(Lightspeed) G Pro X 2 Mouse
- ✅(Lightspeed) G305 Mouse
- ✅(Lightspeed) G705 Mouse 
- 🟨(Lightspeed) G515 TKL (after closing https://github.com/strain08/LGSTrayBattery/issues/3)

## OS Compatibility
- currently Windows 11 only

## Known issues
- `enableRichToolsips = true` coud result in tooltips not closing properly when using fullscreen apps<br>
  Set this to `false` for classic windows tooltips. There is no specific functionality implemented for this.<br>
  It will default to `false` in versions after v3.1.3

## Issue reporting
1. Edit `appsettings.toml`
```
[Logging]
enabled = true
verbose = true
```
2. Reproduce issue
3. Post the resulting `diagnostic.log` along with the issue description and repro.
     
## Notes
- hidapi.dll is same as andy's, SHA256: `38bda32f593c054cacaf95bebce36f9bacc7fbd0740f7b6f72f6d368fbc84b4d`
