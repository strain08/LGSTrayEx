using LGSTrayHID.HidApi;
using System.Threading.Channels;

namespace LGSTrayHID.Protocol;

/// <summary>
/// Handles sending HID++ commands and correlating responses.
/// Eliminates duplication across WriteRead10, WriteRead20, and Ping20.
/// <remarks>
/// <br>Semaphore ensures only one thread can send a command and wait for a response at a time.</br>
/// <br>1. Thread A sends command X, starts reading from _responseChannel waiting for response X</br>
/// <br>2. Thread B sends command Y, starts reading from _responseChannel waiting for response Y</br>
/// <br>3. Response Y arrives first</br>
/// <br>4. Problem: Thread A might consume response Y from the channel(even though its matcher would reject it), causing Thread B to timeout</br>
/// <br>5. Or worse: if matchers are loose, Thread A might incorrectly accept Thread B's response</br>/// 
/// </remarks>
/// </summary>
public class CommandResponseCorrelator
{    
    private readonly SemaphoreSlim _semaphore;
    private readonly ChannelReader<byte[]> _responseChannel;

    public CommandResponseCorrelator(SemaphoreSlim semaphore, ChannelReader<byte[]> responseChannel)
    {
        _semaphore = semaphore;
        _responseChannel = responseChannel;
    }

    /// <summary>
    /// Sends a HID++ 1.0 command (byte[]) and waits for a matching response.
    /// </summary>
    /// <param name="device">HID device to write to</param>
    /// <param name="command">Command byte array to send</param>
    /// <param name="matcher">Predicate to match response to request</param>
    /// <param name="timeout">Timeout in milliseconds</param>
    /// <param name="earlyExit">Optional early exit condition (e.g., for HID++ 1.0 errors)</param>
    /// <returns>Matching response or empty array on timeout</returns>
    public async Task<byte[]> SendHidpp10AndWaitAsync(
        HidDevicePtr device,
        byte[] command,
        Func<byte[], bool> matcher,
        int timeout,
        Func<byte[], bool>? earlyExit = null)
    {
        bool locked = await _semaphore.WaitAsync(100);
        if (!locked)
        {
            return Array.Empty<byte>();
        }

        try
        {
            await device.WriteAsync(command);

            using CancellationTokenSource cts = new();
            cts.CancelAfter(timeout);

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    byte[] response = await _responseChannel.ReadAsync(cts.Token);

                    // Check early exit condition (e.g., HID++ 1.0 error)
                    if (earlyExit != null && earlyExit(response))
                    {
                        break;
                    }

                    // Check if response matches request
                    if (matcher(response))
                    {
                        return response;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return Array.Empty<byte>();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Sends a HID++ 2.0 command (Hidpp20) and waits for a matching response.
    /// </summary>
    /// <param name="device">HID device to write to</param>
    /// <param name="command">Hidpp20 command to send</param>
    /// <param name="matcher">Predicate to match response to request</param>
    /// <param name="timeout">Timeout in milliseconds</param>
    /// <param name="earlyExit">Optional early exit condition (e.g., for HID++ 1.0 errors)</param>
    /// <returns>Matching response or empty Hidpp20 on timeout</returns>
    public async Task<Hidpp20> SendHidpp20AndWaitAsync(
        HidDevicePtr device,
        Hidpp20 command,
        Func<Hidpp20, bool> matcher,
        int timeout,
        Func<Hidpp20, bool>? earlyExit = null)
    {
        bool locked = await _semaphore.WaitAsync(100);
        if (!locked)
        {
            return Hidpp20.Empty;
        }

        try
        {
            await device.WriteAsync((byte[])command);

            using CancellationTokenSource cts = new();
            cts.CancelAfter(timeout);

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    byte[] responseBytes = await _responseChannel.ReadAsync(cts.Token);
                    Hidpp20 response = (Hidpp20)responseBytes; // Single cast using implicit operator

                    // Check early exit condition (e.g., HID++ 1.0 error)
                    if (earlyExit != null && earlyExit(response))
                    {
                        break;
                    }

                    // Check if response matches request
                    if (matcher(response))
                    {
                        return response;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return Hidpp20.Empty;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
