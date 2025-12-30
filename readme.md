# LGSTrayBattery (strain08 fork)

**Note:** This is a community-maintained fork of the original 
[andyvorld/LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery).
The original project appears unmaintained since February 2024. This fork 
continues development with bug fixes and new features.
#### Check the original page for detailed documentation.

## Install
[![GitHub Release](https://img.shields.io/github/v/release/strain08/LGSTrayBattery?sort=semver)](https://github.com/strain08/LGSTrayBattery/releases/latest)

## Configuration
See included [settings file](https://github.com/strain08/LGSTrayBattery/blob/master/LGSTrayUI/appsettings.toml).

# New features and fixes
- see [release notes](https://github.com/strain08/LGSTrayBattery/releases/latest)

# Device compatibility
Tested and working:
- ✅(Bolt) MX Keys S
- ✅(Bolt) MX Anywhere 3S
- ✅(Bolt) MX Master 3
- ✅(Unifying) M330 Silent
- ✅(Lightspeed) G305 Mouse (both Native and GHUB)
- 🟨(Lightspeed) G515 TKL (after closing https://github.com/strain08/LGSTrayBattery/issues/1)

# OS Compatibility
- currently working only on Windows 11

# Troubleshooting
- LogiOptions+ co-op is partially supported. Some erratic behaviour can occur if it runs in parralel with LGSTray, like sudden 0% battery report or device not detected. Needs further exploration.
- for any other issues edit `appsettings.toml` and post the resulting `diagnostic.log` along with the issue:
```
[Logging]
enabled = true
verbose = true
```

# Notes
- hidapi.dll is same as andy's, SHA256: `38bda32f593c054cacaf95bebce36f9bacc7fbd0740f7b6f72f6d368fbc84b4d`
