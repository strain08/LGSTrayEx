using LGSTrayHID.HidApi;
using LGSTrayHID.Routing;
using LGSTrayPrimitives;

using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID.Protocol;

/// <summary>
/// Manages HID message reading threads and lifecycle.
/// Handles two parallel read threads (SHORT and LONG messages) with proper cleanup.
/// </summary>
public class HidMessageChannel : IDisposable
{
    private readonly HidMessageRouter _router;
    private readonly CancellationTokenSource _cancellationSource = new();
    private readonly List<Thread> _readerThreads = new();
    private readonly TaskCompletionSource<bool> _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const int DEVICE_READ_TIMEOUT_MS = 100;
    private const int THREAD_EXIT_TIMEOUT_MS = 5000; // 5 seconds

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
        Thread shortThread = new(async () =>
        {
            // Signal ready immediately when first thread starts
            _readySignal.TrySetResult(true);
            await ReadThreadAsync(devShort, 7);
        })
        {
            Priority = ThreadPriority.BelowNormal,
            Name = "HID-SHORT-Reader"
        };
        _readerThreads.Add(shortThread);
        shortThread.Start();

        Thread longThread = new(async () => { await ReadThreadAsync(devLong, 20); })
        {
            Priority = ThreadPriority.BelowNormal,
            Name = "HID-LONG-Reader"
        };
        _readerThreads.Add(longThread);
        longThread.Start();
    }

    /// <summary>
    /// Waits until HID read threads are ready to process messages.
    /// Returns immediately if threads are already started.
    /// </summary>
    /// <returns>A task that completes when the read threads are ready</returns>
    public Task WaitUntilReadyAsync() => _readySignal.Task;

    /// <summary>
    /// Reads HID messages in a loop until cancellation is requested.
    /// Handles timeout (0 = retry), error (<0 = retry with backoff, then stop), and success (>0 = process).
    /// </summary>
    private async Task ReadThreadAsync(HidDevicePtr device, int bufferSize)
    {
        byte[] buffer = new byte[bufferSize];
        CancellationToken ct = _cancellationSource.Token;
        int consecutiveErrors = 0;
        const int MAX_CONSECUTIVE_ERRORS = 5;
        const int ERROR_RETRY_DELAY_MS = 500;

        while (!ct.IsCancellationRequested)
        {
            var bytesRead = device.Read(buffer, bufferSize, DEVICE_READ_TIMEOUT_MS);

            // Check cancellation immediately after blocking read
            if (ct.IsCancellationRequested) break;

            if (bytesRead < 0)
            {
                consecutiveErrors++;

                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                {
                    // Persistent error - device likely disconnected or unrecoverable issue
                    DiagnosticLogger.LogError(
                        $"HID read error on {bufferSize}b thread after {consecutiveErrors} attempts " +
                        $"(return code: {bytesRead}). Thread exiting. " +
                        $"Device may be disconnected or driver issue."
                    );
                    break;
                }

                // Transient error - retry with delay
                DiagnosticLogger.LogWarning(
                    $"HID read error on {bufferSize}b thread (return code: {bytesRead}), " +
                    $"attempt {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS}. Retrying in {ERROR_RETRY_DELAY_MS}ms..."
                );

                try
                {
                    await Task.Delay(ERROR_RETRY_DELAY_MS, ct);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation requested during delay
                    break;
                }
                continue;
            }

            // Reset error counter on successful read (including timeouts)
            if (consecutiveErrors > 0)
            {
                DiagnosticLogger.Log($"HID read on {bufferSize}b thread recovered after {consecutiveErrors} errors");
                consecutiveErrors = 0;
            }

            if (bytesRead == 0) continue;  // Timeout - retry

            LogRawMessage(buffer, bytesRead, bufferSize);

            // Create a copy of the message to prevent data corruption
            // Channel data  may be overwritten before being read by the router !
            // as the router await only wait for the write to complete, not the read.
            byte[] messageCopy = new byte[bytesRead];
            Array.Copy(buffer, messageCopy, bytesRead);

            await _router.RouteMessageAsync(messageCopy);
        }

        HidClose(device);
    }


    /// <summary>
    /// Logs raw HID messages for debugging
    /// Skips common ping responses (0x10 0x?? 0x00) to reduce noise.
    /// </summary>
    private static void LogRawMessage(byte[] buffer, int bytesRead, int bufferSize)
    {
        // Skip common ping responses AND check if verbose enabled before allocating
        if ((buffer[0] != 0x10 || buffer[2] != 0x00) && DiagnosticLogger.IsVerboseEnabled)
        {
            // Only allocate string if we're actually going to log it
            string hex = string.Join(" ", buffer.Take(Math.Min(7, bytesRead)).Select(b => $"{b:X02}"));
            DiagnosticLogger.Verbose($"DEBUG RAW [{bufferSize}b]: {hex}");
        }
    }


    /// <summary>
    /// Stops the read threads by triggering cancellation.
    /// Waits for threads to exit gracefully before returning.
    /// </summary>
    public void Dispose()
    {
        DiagnosticLogger.Log("HidMessageChannel.Dispose starting");

        // Signal threads to stop via cancellation token
        _cancellationSource.Cancel();

        // Wait for all threads to exit
        foreach (var thread in _readerThreads)
        {
            if (thread.IsAlive)
            {
                bool joined = thread.Join(THREAD_EXIT_TIMEOUT_MS);
                if (!joined)
                {
                    DiagnosticLogger.LogWarning($"HidMessageChannel: Thread '{thread.Name}' did not exit within {THREAD_EXIT_TIMEOUT_MS}ms timeout");
                }
                else
                {
                    DiagnosticLogger.Log($"HidMessageChannel: Thread '{thread.Name}' exited successfully");
                }
            }
        }

        _readerThreads.Clear();
        _cancellationSource.Dispose();

        DiagnosticLogger.Log("HidMessageChannel.Dispose completed");
    }
}
