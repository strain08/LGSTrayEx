using LGSTrayHID.HidApi;
using LGSTrayHID.Protocol;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID
{
    public class HidppDevices : IDisposable
    {
        public HidDevicePtr DevShort { get; private set; } = IntPtr.Zero;
        public HidDevicePtr DevLong { get; private set; } = IntPtr.Zero;
        public IReadOnlyDictionary<ushort, HidppDevice> DeviceCollection => _deviceCollection;
        public const byte SW_ID = 0x0A;
        private byte PING_PAYLOAD = 0x55;

        private bool _isReading = true;
        private const int READ_TIMEOUT = 100;
        private const int TASK_DELAY = 2000;
        private const int PING_ENUMERATE_DELAY = 5000;

        private readonly Dictionary<ushort, HidppDevice> _deviceCollection = [];
        

        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly SemaphoreSlim _initSemaphore = new(1, 1); // Ensure sequential device initialization
        private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        private readonly CommandResponseCorrelator _correlator;
        

        private int _disposeCount = 0;
        public bool Disposed => _disposeCount > 0;

        public HidppDevices()
        {
            _correlator = new CommandResponseCorrelator(_semaphore, _channel.Reader);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
                // TEST HARNESS: Log disposal with device count
                DiagnosticLogger.Log($"HidppDevices.Dispose called - Device count: {_deviceCollection.Count}");
                _isReading = false;

                DevShort = IntPtr.Zero;
                DevLong = IntPtr.Zero;
            }
        }

        ~HidppDevices()
        {
            Dispose(disposing: false);
        }

        public async Task SetDevShort(nint devShort)
        { 
            if (DevShort != IntPtr.Zero)
            {
                throw new ReadOnlyException();
            }
            DevShort = devShort;
            await SetUp();
        }

        public async Task SetDevLong(nint devLong)
        {
            if (DevLong != IntPtr.Zero)
            {
                throw new ReadOnlyException();
            }
            DevLong = devLong;
            await SetUp();
        }

        private async Task ReadThread(HidDevicePtr dev, int bufferSize)
        {
            byte[] buffer = new byte[bufferSize];
            while(_isReading)
            {
                var ret = dev.Read(buffer, bufferSize, READ_TIMEOUT);
                if (!_isReading) { break; }

                if (ret < 0)
                {
                    break;
                }
                else if (ret == 0)
                {
                    continue;
                }

#if VERBOSE
                // Log ALL messages for debugging (first 7 bytes)
                string hex = string.Join(" ", buffer.Take(Math.Min(7, ret)).Select(b => $"{b:X02}"));
                if (buffer[0] != 0x10 || buffer[2] != 0x00) // Skip common ping responses to reduce noise
                {
                    DiagnosticLogger.Log($"DEBUG RAW [{bufferSize}b]: {hex}");
                }
#endif

                await ProcessMessage(buffer);
            }

            HidClose(dev);
        }

        private async Task ProcessMessage(byte[] buffer)
        {
            Hidpp20 message = buffer;

            // Handle device announcements (both connection and disconnection)
            if (message.IsDeviceAnnouncement())
            {
                byte announcementDeviceIdx = buffer[1];
                bool isDeviceOn = (buffer[4] & 0x40) == 0;  // Bit 6 clear = ON, set = OFF

                if (isDeviceOn)
                {
                    // Device turned ON or paired
                    DiagnosticLogger.Log($"[Device ON Event] Index: {announcementDeviceIdx}, " +
                                        $"Params: [0x{buffer[3]:X02} 0x{buffer[4]:X02} 0x{buffer[5]:X02} 0x{buffer[6]:X02}]");

                    // Existing device creation logic                    
                    _deviceCollection[announcementDeviceIdx] = new(this, announcementDeviceIdx);
                    byte capturedIdx = announcementDeviceIdx;
                    new Thread(async () =>
                    {
                        try
                        {
                            await Task.Delay(1000);

                            // Wait for previous device initialization to complete (sequential init)
                            await _initSemaphore.WaitAsync();
                            try
                            {
                                DiagnosticLogger.Log($"Starting initialization for device {capturedIdx}");
                                await _deviceCollection[capturedIdx].InitAsync();
                                DiagnosticLogger.Log($"Completed initialization for device {capturedIdx}");
                            }
                            finally
                            {
                                _initSemaphore.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.LogError($"Device {capturedIdx} initialization failed: {ex.Message}");
                        }
                    }).Start();
                    
                }
                else
                {
                    // Device turned OFF or unpaired
                    string deviceName = "Unknown";
                    if (_deviceCollection.TryGetValue(announcementDeviceIdx, out HidppDevice? offlineDevice))
                    {
                        deviceName = offlineDevice.DeviceName;
                    }

                    DiagnosticLogger.Log($"[Device OFF Event] Index: {announcementDeviceIdx}, " +
                                        $"Name: {deviceName}, " +
                                        $"Params: [0x{buffer[3]:X02} 0x{buffer[4]:X02} 0x{buffer[5]:X02} 0x{buffer[6]:X02}]");

                    // Send UpdateMessage to mark device as offline in UI
                    string deviceId = "";
                    if (offlineDevice != null)
                    {
                        deviceId = offlineDevice.Identifier;
                    }

                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        HidppManagerContext.Instance.SignalDeviceEvent(
                            IPCMessageType.UPDATE,
                            new UpdateMessage(
                                deviceId: deviceId,
                                batteryPercentage: -1,  // Convention: -1 = offline/unknown
                                powerSupplyStatus: PowerSupplyStatus.POWER_SUPPLY_STATUS_UNKNOWN,
                                batteryMVolt: 0,
                                updateTime: DateTimeOffset.Now,
                                mileage: -1
                            )
                        );
                        DiagnosticLogger.Log($"[{deviceName}] Device offline notification sent to UI");
                    }

                    // Note: Device stays in collection and UI (as per user requirement)
                }

                return; // Don't send to response channel
            }

            // Check if this is a wireless status or battery event (unsolicited, not a response)
            byte deviceIdx = message.GetDeviceIdx();
            if (_deviceCollection.TryGetValue(deviceIdx, out HidppDevice? device))
            {
                // Try to route as wireless status event (0x1D4B - BOLT receivers)
                if (device.TryHandleWirelessStatusEvent(message))
                {
                    return; // Event handled, don't send to response channel
                }

                // Try to route as battery event to the device
                if (await device.TryHandleBatteryEventAsync(message))
                {
                    return; // Event handled, don't send to response channel
                }
            }

            // Not an event - must be a query response, send to response channel
            await _channel.Writer.WriteAsync(buffer);
        }

        public async Task<byte[]> WriteRead10(HidDevicePtr hidDevicePtr, byte[] buffer, int timeout = 100)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

            return await _correlator.SendHidpp10AndWaitAsync(
                hidDevicePtr,
                buffer,
                matcher: response => (response[2] == 0x8F) || (response[2] == buffer[2]),
                timeout: timeout,
                earlyExit: null
            );
        }

        public async Task<Hidpp20> WriteRead20(HidDevicePtr hidDevicePtr, Hidpp20 buffer, int timeout = 100, bool ignoreHID10 = true)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

            return await _correlator.SendHidpp20AndWaitAsync(
                hidDevicePtr,
                buffer,
                matcher: response => (response.GetFeatureIndex() == buffer.GetFeatureIndex()) &&
                                     (response.GetSoftwareId() == SW_ID),
                timeout: timeout,
                earlyExit: ignoreHID10 ? null : response => response.IsError()
            );
        }

        public async Task<bool> Ping20(byte deviceId, int timeout = 100, bool ignoreHIDPP10 = true)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

            byte pingPayload = ++PING_PAYLOAD;
            Hidpp20 command = Hidpp20Commands.Ping(deviceId, pingPayload);

            Hidpp20 ret = await _correlator.SendHidpp20AndWaitAsync(
                DevShort,
                command,
                matcher: response => (response.GetFeatureIndex() == 0x00) &&
                                    (response.GetSoftwareId() == SW_ID) &&
                                    (response.GetParam(2) == pingPayload),
                timeout: timeout,
                earlyExit: ignoreHIDPP10 ? null : response => response.IsError()
            );

            return ret.Length > 0;
        }

        private async Task SetUp()
        {
            if ((DevShort == IntPtr.Zero) || (DevLong == IntPtr.Zero))
            {
                return;
            }            

            DiagnosticLogger.Log("Device ready");

            Thread t1 = new(async () => { await ReadThread(DevShort, 7); })
            {
                Priority = ThreadPriority.BelowNormal
            };
            t1.Start();

            Thread t2 = new(async () => { await ReadThread(DevLong, 20); })
            {
                Priority = ThreadPriority.BelowNormal
            };
            t2.Start();

            // Wait for read threads to be ready before sending commands
            await Task.Delay(500);

            byte[] ret;

            // Note: DJ protocol (0x20/0x21) does not work with BOLT receivers.
            // BOLT uses HID++ 2.0 Feature 0x1D4B (Wireless Device Status) for connection events.
            // Events are automatically enabled by the battery enable command (HID++ 1.0 register 0x00).

            // Enable receiver notifications for device connection/disconnection events
            // Send EnableBatteryReports to the RECEIVER (0xFF) to enable device on/off announcements
            try
            {
                // This enables the receiver to send 0x41 announcements when devices turn on/off
                // Same command as per-device battery enable, but sent to receiver index 0xFF
                var enableReceiverNotifications = Hidpp10Commands.EnableBatteryReports(0xFF);
                ret = await WriteRead10(DevShort, enableReceiverNotifications, 1000);
                DiagnosticLogger.Log($"Receiver EnableBatteryReports response: {BitConverter.ToString(ret)}");

                // Also try enabling all notification types (0x0F = all reports)
                byte[] enableAllReports = new byte[7]
                {
                    0x10,  // HID++ 1.0
                    0xFF,  // Receiver
                    0x80,  // SET_REGISTER
                    0x00,  // Register 0x00 (ENABLE_REPORTS)
                    0x0F,  // Enable ALL report types (battery + wireless + others)
                    0x0F,  // Confirmation
                    0x00   // Padding
                };
                ret = await WriteRead10(DevShort, enableAllReports, 1000);
                DiagnosticLogger.Log($"Receiver EnableAllReports response: {BitConverter.ToString(ret)}");

                DiagnosticLogger.Log("Receiver device on/off notifications enabled");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log($"Failed to enable receiver notifications: {ex.Message}");
                // Non-fatal - continue with standard initialization
            }

            // Query receiver for number of connected devices
            ret = await WriteRead10(DevShort, Hidpp10Commands.QueryDeviceCount(), 1000);
            byte numDeviceFound = 0;
            if ((ret[2] == ReceiverCommand.QUERY_DEVICE_COUNT) && (ret[3] == ReceiverCommand.SUB_COMMAND))
            {
                numDeviceFound = ret[5];
            }

            if (numDeviceFound > 0)
            {
                // Force connected devices to announce themselves
                ret = await WriteRead10(DevShort, Hidpp10Commands.ForceDeviceAnnounce(), 1000);
            }

            await Task.Delay(TASK_DELAY);

            if (_deviceCollection.Count == 0)
            {
                // Fail to enumerate devices
                for (byte i = 1; i <= 6; i++)
                {
                    var ping = await Ping20(i, PING_ENUMERATE_DELAY, false);
                    if (ping)
                    {
                        var deviceIdx = i;
                        _deviceCollection[deviceIdx] = new(this, deviceIdx);
                    }
                }

                foreach ((_, var device) in _deviceCollection)
                {
                    try
                    {
                        DiagnosticLogger.Log($"Starting fallback initialization for device {device.DeviceIdx}");
                        await device.InitAsync();
                        DiagnosticLogger.Log($"Completed fallback initialization for device {device.DeviceIdx}");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.LogError($"Device {device.DeviceIdx} fallback initialization failed: {ex.Message}");
                    }
                }
            }
        }
    }
}
