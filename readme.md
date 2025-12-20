# LGSTrayBattery (strain08 fork)

**Note:** This is a community-maintained fork of the original 
[andyvorld/LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery).
The original project appears unmaintained since February 2024. This fork 
continues development with bug fixes and new features.
#### Check the original page for detailed documentation.

## Install
[![GitHub Release](https://img.shields.io/github/v/release/strain08/LGSTrayBattery?sort=semver)](https://github.com/strain08/LGSTrayBattery/releases/latest)

# New features and fixes
- see release notes

# Troubleshooting
- LogiOptions+ co-op is partially supported. Some erratic behaviour can occur if it runs in parralel with LGSTray, like sudden 0% battery report or device not detected. Needs further exploration.
- for any other issues run with --log --verbose flags and attach the diagnostic.log file and/or crashlog files. Disable LogiOptions+ service and reboot before gathering log and posting issues.

# Notes
- I currently own only Bolt (MX Keys S, MX Anywhere 3S, MX Master 3) and Unifying (M330) so testing is limited to these
- hidapi.dll is same as andy's, SHA256: `38bda32f593c054cacaf95bebce36f9bacc7fbd0740f7b6f72f6d368fbc84b4d`
