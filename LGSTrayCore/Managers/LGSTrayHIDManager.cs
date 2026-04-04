using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;

namespace LGSTrayCore.Managers;

public class LGSTrayHIDManager : IDeviceManager, IHostedService, IDisposable
{
    #region IDisposable
    private Func<Task>? _disposeSubs;
    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                _ = _disposeSubs?.Invoke();
                _disposeSubs = null;
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~LGSTrayHIDDaemon()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion

    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _daemonCts;

    private readonly IDistributedSubscriber<IPCMessageType, IPCMessage> _subscriber;
    private readonly IPublisher<IPCMessage> _deviceEventBus;

    public LGSTrayHIDManager(
        IDistributedSubscriber<IPCMessageType, IPCMessage> subscriber,
        IPublisher<IPCMessage> deviceEventBus
    )
    {
        _subscriber = subscriber;
        _deviceEventBus = deviceEventBus;
    }

    private string BuildHidDaemonArguments()
    {
        var args = new List<string> { Environment.ProcessId.ToString() };

        if (DiagnosticLogger.IsEnabled)
        {
            args.Add("--log");
        }

        if (DiagnosticLogger.IsVerboseEnabled)
        {
            args.Add("--verbose");
        }

        return string.Join(" ", args);
    }

    private async Task<int> DaemonLoop()
    {
        _daemonCts = new();

        using Process proc = new();
        string daemonPath = Path.Combine(AppContext.BaseDirectory, "LGSTrayHID.exe");
        proc.StartInfo = new()
        {
            RedirectStandardError = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            FileName = daemonPath,
            Arguments = BuildHidDaemonArguments(),
            UseShellExecute = true,
            CreateNoWindow = true
        };

        DiagnosticLogger.Log($"[LGSTrayHIDManager]: Starting HID daemon: {daemonPath}");

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log($"[LGSTrayHIDManager]: Failed to start HID daemon: {ex.Message}");
            _daemonCts.Dispose();
            _daemonCts = null;
            await Task.Delay(1000);
            return int.MinValue;
        }

        DiagnosticLogger.Log($"[LGSTrayHIDManager]: HID daemon started (PID {proc.Id})");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _daemonCts.Token);
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (Exception)
        {
            if (!proc.HasExited)
            {
                proc.Kill();
            }
        }
        finally
        {
            _daemonCts.Dispose();
            _daemonCts = null;
        }

        DiagnosticLogger.Log($"[LGSTrayHIDManager]: HID daemon exited (exit code {proc.ExitCode})");

        await Task.Delay(1000);
        return proc.ExitCode;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var sub1 = await _subscriber.SubscribeAsync(
            IPCMessageType.INIT,
            x =>
            {
                var initMessage = (InitMessage)x;
                //_logiDeviceCollection.OnInitMessage(initMessage);
                _deviceEventBus.Publish(initMessage);
            },
            cancellationToken
        );

        var sub2 = await _subscriber.SubscribeAsync(
            IPCMessageType.UPDATE,
            x =>
            {
                var updateMessage = (UpdateMessage)x;
                //_logiDeviceCollection.OnUpdateMessage(updateMessage);
                _deviceEventBus.Publish(updateMessage);
            },
            cancellationToken
        );

        _disposeSubs = async () =>
        {
            await sub1.DisposeAsync();
            await sub2.DisposeAsync();
        };

        _ = Task.Run(async () =>
        {
            int fastFailCount = 0;

            while (!_cts.Token.IsCancellationRequested)
            {
                DateTime then = DateTime.Now;
                int ret = await DaemonLoop();

                if (_cts.Token.IsCancellationRequested)
                {
                    break;
                }

                double uptimeSeconds = (DateTime.Now - then).TotalSeconds;

                // Daemon returns -1 on .Kill(), assume its intentional
                if (ret != -1 || uptimeSeconds < 20)
                {
                    fastFailCount++;
                    DiagnosticLogger.Log($"[LGSTrayHIDManager]: HID daemon fast-failed (uptime {uptimeSeconds:F1}s, exit code {ret}, count {fastFailCount}/3)");
                }
                else
                {
                    fastFailCount = 0;
                }

                if (fastFailCount > 3)
                {
                    DiagnosticLogger.Log("[LGSTrayHIDManager]: HID daemon exceeded fast-fail limit — giving up.");
                    break;
                }

                DiagnosticLogger.Log("[LGSTrayHIDManager]: Restarting HID daemon...");
            }
        }, CancellationToken.None);

        return;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();

        return Task.CompletedTask;
    }

    public async Task RediscoverDevices()
    {
        // First, remove all Native HID devices to prevent stale state
        _deviceEventBus.Publish(new RemoveMessage("*NATIVE*", "rediscover_cleanup"));
        DiagnosticLogger.Log("Clearing all Native HID devices before rediscovery");

        // Wait 100ms for removal to propagate through MessagePipe
        await Task.Delay(100);

        // Now restart daemon to rediscover devices fresh
        _daemonCts?.Cancel();

        DiagnosticLogger.Log("Native HID device rediscovery initiated (daemon restart)");
    }
}
