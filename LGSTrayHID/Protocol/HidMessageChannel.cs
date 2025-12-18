using LGSTrayHID.HidApi;
using LGSTrayHID.Routing;
using LGSTrayPrimitives;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID.Protocol
{
    /// <summary>
    /// Manages HID message reading threads and lifecycle.
    /// Handles two parallel read threads (SHORT and LONG messages) with proper cleanup.
    /// </summary>
    public class HidMessageChannel : IDisposable
    {
        private readonly HidMessageRouter _router;
        private bool _isReading = true;
        private const int READ_TIMEOUT = 100;

        public HidMessageChannel(HidMessageRouter router)
        {
            _router = router;
        }

        /// <summary>
        /// Starts two parallel read threads (SHORT and LONG messages).
        /// Threads run at BelowNormal priority to avoid starving UI thread.
        /// </summary>
        /// <param name="devShort">HID device handle for SHORT (7-byte) messages</param>
        /// <param name="devLong">HID device handle for LONG (20-byte) messages</param>
        public void StartReading(HidDevicePtr devShort, HidDevicePtr devLong)
        {
            Thread shortThread = new(async () => { await ReadThreadAsync(devShort, 7); })
            {
                Priority = ThreadPriority.BelowNormal,
                Name = "HID-SHORT-Reader"
            };
            shortThread.Start();

            Thread longThread = new(async () => { await ReadThreadAsync(devLong, 20); })
            {
                Priority = ThreadPriority.BelowNormal,
                Name = "HID-LONG-Reader"
            };
            longThread.Start();
        }

        /// <summary>
        /// Reads HID messages in a loop until _isReading is false.
        /// Handles timeout (0 = retry), error (<0 = stop), and success (>0 = process).
        /// </summary>
        private async Task ReadThreadAsync(HidDevicePtr device, int bufferSize)
        {
            byte[] buffer = new byte[bufferSize];

            while (_isReading)
            {
                var bytesRead = device.Read(buffer, bufferSize, READ_TIMEOUT);

                if (!_isReading) break;

                if (bytesRead < 0) break;      // Error - stop thread
                if (bytesRead == 0) continue;  // Timeout - retry

                LogRawMessage(buffer, bytesRead, bufferSize);

                await _router.RouteMessageAsync(buffer);
            }

            HidClose(device);
        }


        /// <summary>
        /// Logs raw HID messages for debugging
        /// Skips common ping responses (0x10 0x?? 0x00) to reduce noise.
        /// </summary>
        private static void LogRawMessage(byte[] buffer, int bytesRead, int bufferSize)
        {
            string hex = string.Join(" ", buffer.Take(Math.Min(7, bytesRead)).Select(b => $"{b:X02}"));
            if (buffer[0] != 0x10 || buffer[2] != 0x00) // Skip common ping responses
            {
                DiagnosticLogger.Verbose($"DEBUG RAW [{bufferSize}b]: {hex}");
            }
        }


        /// <summary>
        /// Stops the read threads by setting _isReading to false.
        /// Threads will exit gracefully and close their HID handles.
        /// </summary>
        public void Dispose()
        {
            _isReading = false;
        }
    }
}
