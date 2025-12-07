using LGSTrayHID.HidApi;
using LGSTrayHID.Protocol;
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
        public const byte SW_ID = 0x0A;
        private byte PING_PAYLOAD = 0x55;

        private bool _isReading = true;
        private const int READ_TIMEOUT = 100;
        private const int TASK_DELAY = 2000;
        private const int PING_ENUMERATE_DELAY = 5000;

        private readonly Dictionary<ushort, HidppDevice> _deviceCollection = [];
        public IReadOnlyDictionary<ushort, HidppDevice> DeviceCollection => _deviceCollection;

        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        private HidDevicePtr _devShort = IntPtr.Zero;
        public HidDevicePtr DevShort
        {
            get => _devShort;
        }

        private HidDevicePtr _devLong = IntPtr.Zero;
        public HidDevicePtr DevLong
        {
            get => _devLong;
        }

        private int _disposeCount = 0;
        public bool Disposed => _disposeCount > 0;

        public HidppDevices() { }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
#if DEBUG
                // TEST HARNESS: Log disposal with device count
                LGSTrayPrimitives.DiagnosticLogger.Log($"HidppDevices.Dispose called - Device count: {_deviceCollection.Count}");
#endif
                _isReading = false;

                _devShort = IntPtr.Zero;
                _devLong = IntPtr.Zero;
            }
        }

        ~HidppDevices()
        {
            Dispose(disposing: false);
        }

        public async Task SetDevShort(nint devShort)
        { 
            if (_devShort != IntPtr.Zero)
            {
                throw new ReadOnlyException();
            }
            _devShort = devShort;
            await SetUp();
        }

        public async Task SetDevLong(nint devLong)
        {
            if (_devLong != IntPtr.Zero)
            {
                throw new ReadOnlyException();
            }
            _devLong = devLong;
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

                #if DEBUG
                // Log ALL messages for debugging (first 7 bytes)
                string hex = string.Join(" ", buffer.Take(Math.Min(7, ret)).Select(b => $"{b:X02}"));
                if (buffer[0] != 0x10 || buffer[2] != 0x00) // Skip common ping responses to reduce noise
                {
                    LGSTrayPrimitives.DiagnosticLogger.Log($"DEBUG RAW [{bufferSize}b]: {hex}");
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
                    LGSTrayPrimitives.DiagnosticLogger.Log($"[Device ON Event] Index: {announcementDeviceIdx}, " +
                                        $"Params: [0x{buffer[3]:X02} 0x{buffer[4]:X02} 0x{buffer[5]:X02} 0x{buffer[6]:X02}]");

                    // Existing device creation logic
                    if (true || !_deviceCollection.ContainsKey(announcementDeviceIdx))
                    {
                        _deviceCollection[announcementDeviceIdx] = new(this, announcementDeviceIdx);
                        byte capturedIdx = announcementDeviceIdx;
                        new Thread(async () =>
                        {
                            try
                            {
                                await Task.Delay(1000);
                                await _deviceCollection[capturedIdx].InitAsync();
                            }
                            catch (Exception) { }
                        }).Start();
                    }
                }
                else
                {
                    // Device turned OFF or unpaired
                    string deviceName = "Unknown";
                    if (_deviceCollection.TryGetValue(announcementDeviceIdx, out HidppDevice? offlineDevice))
                    {
                        deviceName = offlineDevice.DeviceName;
                    }

                    LGSTrayPrimitives.DiagnosticLogger.Log($"[Device OFF Event] Index: {announcementDeviceIdx}, " +
                                        $"Name: {deviceName}, " +
                                        $"Params: [0x{buffer[3]:X02} 0x{buffer[4]:X02} 0x{buffer[5]:X02} 0x{buffer[6]:X02}]");

                    // Note: Device stays in collection and UI (as per user requirement)
                    // Future enhancement: Could mark as offline or remove here
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

            bool locked = await _semaphore.WaitAsync(100);
            if (!locked)
            {
                return [];
            }

            try
            {
                await hidDevicePtr.WriteAsync((byte[])buffer);

                CancellationTokenSource cts = new();
                cts.CancelAfter(timeout);

                byte[] ret;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        ret = await _channel.Reader.ReadAsync(cts.Token);

                        if ((ret[2] == 0x8F) || (ret[2] == buffer[2]))
                        {
                            return ret;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                }

                return [];
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<Hidpp20> WriteRead20(HidDevicePtr hidDevicePtr, Hidpp20 buffer, int timeout = 100, bool ignoreHID10 = true)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

            bool locked = await _semaphore.WaitAsync(100);
            if (!locked)
            {
                return (Hidpp20)Array.Empty<byte>();
            }

            try
            {
                await hidDevicePtr.WriteAsync((byte[]) buffer);

                CancellationTokenSource cts = new();
                cts.CancelAfter(timeout);

                Hidpp20 ret;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        ret = await _channel.Reader.ReadAsync(cts.Token);

                        if (!ignoreHID10 && ret.IsError())
                        {
                            // HID++ 1.0 response or timeout
                            break;
                        }

                        if ((ret.GetFeatureIndex() == buffer.GetFeatureIndex()) && (ret.GetSoftwareId() == SW_ID))
                        {
                            return ret;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                }

                return (Hidpp20) Array.Empty<byte>();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<bool> Ping20(byte deviceId, int timeout = 100, bool ignoreHIDPP10 = true)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);

            byte pingPayload = ++PING_PAYLOAD;
            Hidpp20 command = Hidpp20Commands.Ping(deviceId, pingPayload);
            Hidpp20 ret = await WriteRead20(_devShort, command, timeout, ignoreHIDPP10);
            if (ret.Length == 0)
            {
                return false;
            }

            return (ret.GetFeatureIndex() == 0x00) && (ret.GetSoftwareId() == SW_ID) && (ret.GetParam(2) == pingPayload);

            //bool locked = await _semaphore.WaitAsync(100);
            //if (!locked)
            //{
            //    return false;
            //}

            //try
            //{
            //    byte pingPayload = ++PING_PAYLOAD;
            //    Hidpp20 buffer = new byte[7] { 0x10, deviceId, 0x00, 0x10 | SW_ID, 0x00, 0x00, pingPayload };
            //    await _devShort.WriteAsync((byte[])buffer);

            //    CancellationTokenSource cts = new();
            //    cts.CancelAfter(timeout);

            //    Hidpp20 ret;
            //    while (!cts.IsCancellationRequested)
            //    {
            //        try
            //        {
            //            ret = await _channel.Reader.ReadAsync(cts.Token);

            //            if (!ignoreHIDPP10 && (ret.GetFeatureIndex() == 0x8F))
            //            {
            //                // HID++ 1.0 response or timeout
            //                break;
            //            }

            //            if ((ret.GetFeatureIndex() == 0x00) && (ret.GetSoftwareId() == SW_ID) && (ret.GetParam(2) == pingPayload))
            //            {
            //                return true;
            //            }
            //        }
            //        catch (OperationCanceledException) { break; }
            //    }

            //    return false;
            //}
            //finally
            //{
            //    _semaphore.Release();
            //}
        }

        private async Task SetUp()
        {
            if ((_devShort == IntPtr.Zero) || (_devLong == IntPtr.Zero))
            {
                return;
            }
            
#if DEBUG
            Console.WriteLine("Device ready");
#endif

            Thread t1 = new(async () => { await ReadThread(_devShort, 7); })
            {
                Priority = ThreadPriority.BelowNormal
            };
            t1.Start();

            Thread t2 = new(async () => { await ReadThread(_devLong, 20); })
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
                ret = await WriteRead10(_devShort, enableReceiverNotifications, 1000);
                LGSTrayPrimitives.DiagnosticLogger.Log($"Receiver EnableBatteryReports response: {BitConverter.ToString(ret)}");

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
                ret = await WriteRead10(_devShort, enableAllReports, 1000);
                LGSTrayPrimitives.DiagnosticLogger.Log($"Receiver EnableAllReports response: {BitConverter.ToString(ret)}");

                LGSTrayPrimitives.DiagnosticLogger.Log("Receiver device on/off notifications enabled");
            }
            catch (Exception ex)
            {
                LGSTrayPrimitives.DiagnosticLogger.Log($"Failed to enable receiver notifications: {ex.Message}");
                // Non-fatal - continue with standard initialization
            }

            // Query receiver for number of connected devices
            ret = await WriteRead10(_devShort, Hidpp10Commands.QueryDeviceCount(), 1000);
            byte numDeviceFound = 0;
            if ((ret[2] == ReceiverCommand.QUERY_DEVICE_COUNT) && (ret[3] == ReceiverCommand.SUB_COMMAND))
            {
                numDeviceFound = ret[5];
            }

            if (numDeviceFound > 0)
            {
                // Force connected devices to announce themselves
                ret = await WriteRead10(_devShort, Hidpp10Commands.ForceDeviceAnnounce(), 1000);
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
                    await device.InitAsync();
                }
            }
        }
    }
}
