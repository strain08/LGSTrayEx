# Logitech DJ Protocol Integration Plan

## Overview

Add Logitech DJ (Device Juggler) protocol support to LGSTrayBattery for improved wireless device connection/disconnection event handling. DJ protocol provides real-time notifications when devices are paired/unpaired from Unifying/Nano receivers, eliminating the need for polling-based device discovery.

**Approach**: DJ Events + HID++ Features
- Use DJ protocol for device lifecycle events (connection/disconnection)
- Continue using HID++ 2.0 for feature enumeration, battery polling, device metadata
- Support both DJ-capable receivers and direct-connected devices in parallel

**Estimated Effort**: 6-8 hours
**Risk Level**: Medium (new protocol integration)
**Breaking Changes**: None (fully backward compatible)

---

## Background

### What is DJ Protocol?

Logitech DJ (Device Juggler) is a proprietary wireless communication protocol for Unifying and Nano receivers. It provides:
- **Real-time device pairing notifications** (0x41 - device paired)
- **Real-time device unpairing notifications** (0x40 - device unpaired)
- **Connection status updates** (0x42 - link quality, disconnection)
- **Receiver-to-host communication** via HID reports (0x20 SHORT, 0x21 LONG)

### Current vs DJ-Based Detection

| Aspect | Current (HID++ Hotplug) | With DJ Protocol |
|--------|------------------------|------------------|
| Receiver detection | Manual enumeration | Automatic pairing events |
| Device discovery | Ping-based (slow) | Event-driven (instant) |
| Disconnection | USB unplug only | Wireless unpair + USB unplug |
| Latency | ~1-2 seconds | <100ms |
| Wireless status | Not detected | Real-time link status |

### Protocol Documentation

- [Linux Kernel DJ Driver](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-logitech-dj.c)
- [Logitech HID++ 1.0 Specification](https://lekensteyn.nl/files/logitech/logitech_hidpp10_specification_for_Unifying_Receivers.pdf)
- [Logitech Unifying Reverse Engineering](https://lekensteyn.nl/logitech-unifying.html)

---

## Critical Design Decisions

### 1. 0x41 Message Disambiguation

**Problem**: Both HID++ and DJ use 0x41 in byte[2]
- HID++ 0x41: Device announcement (feature index in HID++ messages)
- DJ 0x41: Device paired notification (report type in DJ reports)

**Solution**: Check Report ID in byte[0]
```
if (buffer[0] == 0x20 || buffer[0] == 0x21):
    → DJ notification (process as DJ)
else if (buffer[0] == 0x10 && buffer[2] == 0x41):
    → HID++ device announcement (existing flow)
```

### 2. Receiver Detection Strategy

Use product ID whitelist + capability verification:
- Known receiver PIDs: 0xC52B, 0xC532, 0xC534
- Attempt DJ mode enablement (0x80 command)
- If successful → DJ mode; if fails → fallback to HID++ enumeration

### 3. Device Index Mapping

DJ device index (1-7) maps directly to HID++ device index:
```csharp
// Store DJ metadata
_djDeviceInfo[djIndex] = new DJDeviceInfo
{
    QuadId = 0x1234,        // 16-bit device identifier
    RfReportType = 0x02,    // Device capabilities
    DjIndex = 3             // DJ slot number (1-7)
};

// Use DJ index as HidppDevice index
_deviceCollection[djIndex] = new HidppDevice(this, djIndex);
```

### 4. Parallel Support Strategy

- **Receivers**: Use DJ protocol for device events
- **Direct devices**: Continue using HID++ hotplug
- Both systems coexist without conflict

---

## Implementation Phases

### Phase 1: DJ Protocol Constants and Data Structures

**New File**: `LGSTrayHID/Protocol/DJProtocolConstants.cs`

Add DJ protocol constants:
- Report IDs: `DJ_SHORT = 0x20`, `DJ_LONG = 0x21`
- Report types: `NOTIF_DEVICE_PAIRED = 0x41`, `NOTIF_DEVICE_UNPAIRED = 0x40`, `NOTIF_CONNECTION_STATUS = 0x42`
- Commands: `CMD_SWITCH = 0x80`, `CMD_GET_PAIRED_DEVICES = 0x81`
- Device types: Keyboard, Mouse, Numpad, Presenter, Trackball, Touchpad
- Known receiver PIDs: 0xC52B, 0xC532, 0xC534

**New File**: `LGSTrayHID/Protocol/DJCommands.cs`

Add command builders:
```csharp
public static byte[] EnableDJMode()
{
    return new byte[7] { 0x20, 0xFF, 0x80, 0x00, 0x00, 0x00, 0x00 };
}

public static byte[] QueryPairedDevices()
{
    return new byte[7] { 0x20, 0xFF, 0x81, 0x00, 0x00, 0x00, 0x00 };
}
```

**New File**: `LGSTrayHID/Protocol/DJNotification.cs`

Add notification parser:
```csharp
public readonly struct DJNotification
{
    public byte GetReportId();      // 0x20 or 0x21
    public byte GetDeviceIndex();   // 1-7
    public byte GetReportType();    // 0x40, 0x41, 0x42
    public ushort GetQuadId();      // 16-bit device ID
    public byte GetRfReportType();  // Device capabilities
    public bool IsDevicePaired();
    public bool IsDeviceUnpaired();
    public bool IsConnectionStatus();
}
```

---

### Phase 2: Receiver Detection and DJ Mode Enablement

**Modify**: `LGSTrayHID/HidApi/HidDeviceInfoHelpers.cs`

Add receiver detection:
```csharp
public static bool IsReceiver(this ref HidDeviceInfo deviceInfo)
{
    ushort productId = deviceInfo.ProductId;
    return productId == 0xC52B || productId == 0xC532 || productId == 0xC534;
}
```

**Modify**: `LGSTrayHID/HidppDevices.cs`

Add DJ state tracking:
```csharp
// Fields
private bool _isDJReceiver = false;
private readonly Dictionary<byte, ushort> _djIndexToDeviceIdx = new();
private readonly Dictionary<ushort, DJDeviceInfo> _djDeviceInfo = new();

struct DJDeviceInfo
{
    public ushort QuadId;
    public byte RfReportType;
    public byte DjIndex;
}
```

Update `SetUp()` method:
```csharp
private async Task SetUp()
{
    // Try to enable DJ mode
    bool djModeEnabled = await TryEnableDJMode();

    if (djModeEnabled)
    {
        _isDJReceiver = true;
        await QueryPairedDevices();
        return; // Skip HID++ 1.0 enumeration
    }

    // Fallback to existing HID++ enumeration
    // ... existing code ...
}

private async Task<bool> TryEnableDJMode()
{
    try
    {
        byte[] command = DJCommands.EnableDJMode();
        await _devShort.WriteAsync(command);
        await Task.Delay(100);
        return true;
    }
    catch { return false; }
}
```

---

### Phase 3: DJ Notification Processing

**Modify**: `LGSTrayHID/HidppDevices.cs`

Update `ProcessMessage()` to route DJ notifications:
```csharp
private async Task ProcessMessage(byte[] buffer)
{
    // Check for DJ notification
    if (buffer[0] == 0x20 || buffer[0] == 0x21)
    {
        await ProcessDJNotification(buffer);
        return;
    }

    // Existing HID++ message handling
    Hidpp20 message = buffer;

    if (message.IsDeviceAnnouncement() && ((buffer[4] & 0x40) == 0))
    {
        // ... existing announcement handling ...
    }

    // ... battery events and responses ...
}
```

Add DJ notification handlers:
```csharp
private async Task ProcessDJNotification(byte[] buffer)
{
    DJNotification notification = buffer;

    switch (notification.GetReportType())
    {
        case DJReportType.NOTIF_DEVICE_PAIRED:
            await HandleDevicePaired(notification);
            break;
        case DJReportType.NOTIF_DEVICE_UNPAIRED:
            HandleDeviceUnpaired(notification);
            break;
        case DJReportType.NOTIF_CONNECTION_STATUS:
            HandleConnectionStatus(notification);
            break;
    }
}

private async Task HandleDevicePaired(DJNotification notification)
{
    byte djIndex = notification.GetDeviceIndex();

    // Store DJ metadata
    _djDeviceInfo[djIndex] = new DJDeviceInfo
    {
        QuadId = notification.GetQuadId(),
        RfReportType = notification.GetRfReportType(),
        DjIndex = djIndex
    };

    // Create HidppDevice (same as HID++ announcement)
    _deviceCollection[djIndex] = new HidppDevice(this, djIndex);

    // Initialize asynchronously
    new Thread(async () =>
    {
        await Task.Delay(1000);
        await _deviceCollection[djIndex].InitAsync();
    }).Start();
}

private void HandleDeviceUnpaired(DJNotification notification)
{
    byte djIndex = notification.GetDeviceIndex();

    if (_deviceCollection.TryGetValue(djIndex, out HidppDevice? device))
    {
        // Signal removal to UI (Phase 4)
        HidppManagerContext.Instance.SignalDeviceRemoval(
            device.Identifier, device.DeviceName);

        // Cleanup
        _deviceCollection.Remove(djIndex);
        _djDeviceInfo.Remove(djIndex);
    }
}

private void HandleConnectionStatus(DJNotification notification)
{
    // Connection status byte at offset 6
    // 0x00 = disconnected, 0x01 = connected
    byte status = buffer[6];
    if (status == 0x00)
    {
        HandleDeviceUnpaired(notification);
    }
}
```

---

### Phase 4: RemovalMessage IPC

**Modify**: `LGSTrayPrimitives/IPC/MessageStructs.cs`

Add removal message:
```csharp
public enum IPCMessageType : byte
{
    HEARTBEAT = 0,
    INIT,
    UPDATE,
    REMOVAL  // NEW
}

[MessagePackObject]
public class RemovalMessage(string deviceId, string deviceName) : IPCMessage(deviceId)
{
    [Key(1)]
    public string deviceName = deviceName;
}

// Update union on IPCMessage
[Union(0, typeof(InitMessage))]
[Union(1, typeof(UpdateMessage))]
[Union(2, typeof(RemovalMessage))]  // NEW
public abstract class IPCMessage(string deviceId) { ... }
```

**Modify**: `LGSTrayHID/HidppManagerContext.cs`

Add removal signal method:
```csharp
public void SignalDeviceRemoval(string deviceId, string deviceName)
{
    var removalMessage = new RemovalMessage(deviceId, deviceName);
    HidppDeviceEvent?.Invoke(IPCMessageType.REMOVAL, removalMessage);
}
```

Update `DeviceLeft()` hotplug callback:
```csharp
// Before disposal, signal removal for each device
foreach (var (deviceIdx, device) in deviceCollection)
{
    HidppManagerContext.Instance.SignalDeviceRemoval(
        device.Identifier, device.DeviceName);
}

// Then dispose as before
_deviceMap[containerId].Dispose();
```

---

### Phase 5: UI Integration - Device Removal

**Modify**: `LGSTrayUI/LogiDeviceCollection.cs`

Add removal handler:
```csharp
// Update subscription
_subscriber.Subscribe(x =>
{
    if (x is InitMessage initMessage)
        OnInitMessage(initMessage);
    else if (x is UpdateMessage updateMessage)
        OnUpdateMessage(updateMessage);
    else if (x is RemovalMessage removalMessage)  // NEW
        OnRemovalMessage(removalMessage);
});

// Add handler method
public void OnRemovalMessage(RemovalMessage removalMessage)
{
    Application.Current.Dispatcher.BeginInvoke(() =>
    {
        var device = Devices.FirstOrDefault(dev =>
            dev.DeviceId == removalMessage.deviceId);

        if (device != null)
        {
            Devices.Remove(device);
            DiagnosticLogger.Log($"Device removed from UI - {removalMessage.deviceName}");
        }
    });
}
```

---

### Phase 6: Diagnostics and Logging

**Modify**: `LGSTrayHID/HidppDevices.cs`

Add comprehensive logging:
```csharp
// In SetUp()
if (djModeEnabled)
{
    DiagnosticLogger.Log("==== DJ MODE ENABLED ====");
    DiagnosticLogger.Log("Receiver using DJ protocol for device events");
}
else
{
    DiagnosticLogger.Log("==== HID++ MODE ====");
    DiagnosticLogger.Log("Using HID++ hotplug detection");
}

// In ProcessDJNotification()
DiagnosticLogger.Log($"DJ Notification - Type: 0x{reportType:X02}, " +
                    $"Index: {djIndex}, Quad ID: 0x{quadId:X04}");

// In HandleDevicePaired()
string deviceType = rfReportType switch
{
    0x01 => "Keyboard",
    0x02 => "Mouse",
    0x03 => "Numpad",
    0x08 => "Trackball",
    0x09 => "Touchpad",
    _ => $"Unknown (0x{rfReportType:X02})"
};
DiagnosticLogger.Log($"DJ device paired - Type: {deviceType}");
```

---

## Testing Strategy

### Test Cases

1. **DJ Receiver Detection**
   - Connect Unifying receiver
   - Verify "DJ MODE ENABLED" in logs
   - Verify DJ mode switch command sent

2. **Existing Paired Devices**
   - Start app with devices already paired
   - Verify QueryPairedDevices() triggers
   - Verify NOTIF_DEVICE_PAIRED for each device
   - Verify devices appear in UI

3. **Hot Pairing**
   - Pair new device while app running
   - Verify NOTIF_DEVICE_PAIRED received
   - Verify device initializes and appears in UI

4. **Device Power Off**
   - Turn off wireless device
   - Verify NOTIF_CONNECTION_STATUS (status=0x00)
   - Verify device removed from UI

5. **Device Unpairing**
   - Unpair device via Logitech software
   - Verify NOTIF_DEVICE_UNPAIRED received
   - Verify RemovalMessage sent
   - Verify device removed from UI

6. **Direct-Connected Device**
   - Connect wired Logitech device
   - Verify "HID++ MODE" in logs
   - Verify existing hotplug still works

7. **0x41 Disambiguation**
   - Test both HID++ announcements and DJ paired notifications
   - Verify correct routing based on byte[0]

8. **Battery Continuity**
   - Verify battery polling works for DJ devices
   - Verify battery events still fire
   - Verify no regressions

### Manual Testing Procedure

```powershell
# 1. Build in Debug mode
dotnet build LGSTrayBattery.sln -c Debug

# 2. Run application
dotnet run --project LGSTrayUI/LGSTrayUI.csproj

# 3. Monitor diagnostics.log
tail -f diagnostics.log

# 4. Test scenarios
# - Connect Unifying receiver → Check DJ mode
# - Pair new device → Check NOTIF_DEVICE_PAIRED
# - Turn off device → Check NOTIF_CONNECTION_STATUS
# - Unpair device → Check NOTIF_DEVICE_UNPAIRED
```

---

## Implementation Order

1. **Phase 1**: DJ protocol constants and structures (foundation)
2. **Phase 2**: Receiver detection and DJ mode enablement
3. **Phase 3**: DJ notification processing (core functionality)
4. **Phase 4**: RemovalMessage IPC (device removal infrastructure)
5. **Phase 5**: UI integration (user-visible removal)
6. **Phase 6**: Diagnostics and logging (debugging)
7. **Testing**: Comprehensive validation

### Incremental Testing

- After Phase 1: Verify compilation
- After Phase 2: Test DJ mode detection (logs only)
- After Phase 3: Test device paired notifications
- After Phase 4+5: Test complete removal flow
- After Phase 6: Full integration testing

---

## Risk Mitigation

### Known Risks

| Risk | Mitigation |
|------|-----------|
| Some receivers don't support DJ | Graceful fallback to HID++ enumeration |
| Device index collision | DJ and HID++ use same index space by design |
| Missing removal notifications | HID++ hotplug still fires for USB disconnect |
| Battery polling compatibility | DJ only affects discovery; HID++ features unchanged |

### Rollback Strategy

If DJ protocol causes issues:
1. Add configuration flag: `enableDJ = false` in appsettings.toml
2. Wrap DJ code in `if (settings.EnableDJ)` checks
3. Complete rollback: Revert all changes via git

---

## Success Criteria

✅ DJ mode detected for Unifying/Nano receivers
✅ Existing paired devices discovered on startup
✅ Hot pairing triggers instant device addition
✅ Device unpair/disconnect removes from UI
✅ Direct-connected devices continue working
✅ Battery polling/events unaffected
✅ 0x41 messages correctly disambiguated
✅ Zero regressions in existing functionality

---

## Critical Files

**New Files**:
- `LGSTrayHID/Protocol/DJProtocolConstants.cs` - DJ protocol constants
- `LGSTrayHID/Protocol/DJCommands.cs` - DJ command builders
- `LGSTrayHID/Protocol/DJNotification.cs` - DJ notification parser

**Modified Files**:
- `LGSTrayHID/HidApi/HidDeviceInfoHelpers.cs` - Add IsReceiver() detection
- `LGSTrayHID/HidppDevices.cs` - DJ mode enablement, notification processing
- `LGSTrayPrimitives/IPC/MessageStructs.cs` - Add RemovalMessage
- `LGSTrayHID/HidppManagerContext.cs` - Add SignalDeviceRemoval(), update DeviceLeft()
- `LGSTrayUI/LogiDeviceCollection.cs` - Add OnRemovalMessage() handler
