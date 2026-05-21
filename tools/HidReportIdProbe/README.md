# HidReportIdProbe

Standalone console tool to validate the Windows HID descriptor read path (`HidD_GetPreparsedData` + `HidP_GetCaps` + `HidP_GetValueCaps` / `HidP_GetButtonCaps`)
against the current hidapi blind-write probe in `LGSTrayHID/Centurion/CenturionTransportFactory.cs`.

## Build
Requires .NET 9 SDK
Build needs `hidapi.dll` copied from `LGSTrayHID/HidApi/libhidapi/`

## CLI flags

| Flag | Meaning |
|------|---------|
| `--vid=XXXX` | Vendor ID to enumerate (hex). Default `046D` (Logitech). |
| `--blind` | After descriptor read, also do a hidapi blind-write probe for report IDs `0x10`, `0x11`, `0x50`, `0x51` and diff the two answers. |
| `--no-pause` | Skip the "Press Enter to close" prompt at the end. |

## What "good" looks like

For a Bolt or Unifying receiver, the HID++ vendor-specific interface (page `0xFF00`, usage `0x0002`) should print:

```
HidP input  IDs: 0x11
HidP output IDs: 0x11
*** HID++/Centurion IDs declared: 0x11
```

Some receivers split SHORT/LONG into separate top-level collections, in which case a second entry on the same interface with usage `0x0001` declares `0x10`.

For a G522 dongle, the Centurion interface (page `0xFFA0`, usage `0x0001`) should print:

```
HidP input  IDs: 0x50
HidP output IDs: 0x50
*** HID++/Centurion IDs declared: 0x50
```

For a PRO X 2 dongle, `0x51` in the same place.

With `--blind`, the last line should read `diff: blind probe and HidP descriptor agree` for HID++/Centurion interfaces, and `(none)` accepted for non-HID++ interfaces.

## What it does not validate

- Per-report-ID size (we only enumerate declared IDs, not the bit-summed length). Solaar additionally checks `63 * 8 == get_input_report_size(0x51)` etc. Easy to add via `BitSize * ReportCount` summed across the matching caps.
- Production handle coexistence — this tool's `CreateFile` opens with `dwDesiredAccess=0` (metadata-only). Production code would still open with hidapi for read/write; verify the two coexist with the LGSTray app running in parallel.

## File map

- `HidReportIdProbe.csproj` — net9.0 console project; published as self-contained `win-x64` single-file
- `HidApiInterop.cs` — minimal hidapi P/Invoke for enumeration and the optional blind probe
- `HidPInterop.cs` — Win32 HID descriptor P/Invoke (`hid.dll`, `kernel32.dll`)
- `Program.cs` — enumerate, descriptor read, optional blind probe, diff; dual-writes to console and timestamped log file
- `publish/HidReportIdProbe.exe` — redistributable single-file build (created by `dotnet publish`)
