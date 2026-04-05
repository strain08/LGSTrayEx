using LGSTrayCore.Interfaces;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LGSTrayCore.Managers;

public class LGSTrayHIDManager : IDeviceManager, IHostedService, IDisposable
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);

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
        IPublisher<IPCMessage> deviceEventBus)
    {
        _subscriber = subscriber;
        _deviceEventBus = deviceEventBus;
    }

    private enum DaemonExitReason
    {
        Normal,       // Process exited on its own
        Killed,       // We killed the process (intentional, e.g. rediscover or stop)
        LaunchFailed, // Failed to start — retriable
        BlockedByOS,  // OS blocked launch (SmartScreen/MOTW) — permanent, give up
    }

    private readonly struct DaemonResult
    {
        public int ExitCode { get; init; }        
        public DaemonExitReason Reason { get; init; }
        
        public static DaemonResult Exited(int exitCode) => new() { 
            ExitCode = exitCode, 
            Reason = DaemonExitReason.Normal 
        };
        public static DaemonResult WasKilled() => new() { 
            ExitCode = -1, 
            Reason = DaemonExitReason.Killed 
        };
        public static DaemonResult Failed() => new() { 
            Reason = DaemonExitReason.LaunchFailed 
        };
        public static DaemonResult Blocked() => new() {
            Reason = DaemonExitReason.BlockedByOS 
        };
    }

    private static string BuildHidDaemonArguments()
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

    private async Task<DaemonResult> DaemonLoop()
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
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED (1223): OS blocked the process (SmartScreen, MOTW, antivirus).
            // Retrying will not help — show a message box and give up.           
            DiagnosticLogger.LogError($"[LGSTrayHIDManager]: HID daemon blocked by OS (ERROR_CANCELLED).");
            _ = MessageBox(
                nint.Zero,
                $"Windows blocked LGSTrayHID.exe from starting (ERROR_CANCELLED).\n\n" +
                $"You probably need to unblock the app files in Powershell:\n" +
                $"Get-ChildItem \"{AppContext.BaseDirectory}\" | Unblock-File\n",
                "LGSTray — HID daemon unable to start",
                0x10 /* MB_ICONERROR */
            );
            _daemonCts.Dispose();
            _daemonCts = null;
            return DaemonResult.Blocked();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"[LGSTrayHIDManager]: Failed to start HID daemon: {ex.Message}");
            _daemonCts.Dispose();
            _daemonCts = null;
            await Task.Delay(1000);
            return DaemonResult.Failed();
        }

        DiagnosticLogger.Log($"[LGSTrayHIDManager]: HID daemon started (PID {proc.Id})");

        bool wasKilled = false;
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
                wasKilled = true;
            }
        }
        finally
        {
            _daemonCts.Dispose();
            _daemonCts = null;
        }

        DiagnosticLogger.Log($"[LGSTrayHIDManager]: HID daemon exited (exit code {proc.ExitCode})");

        await Task.Delay(1000);
        return wasKilled ? DaemonResult.WasKilled() : DaemonResult.Exited(proc.ExitCode);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var sub1 = await _subscriber.SubscribeAsync(
            IPCMessageType.INIT,
            x =>
            {
                var initMessage = (InitMessage)x;
                _deviceEventBus.Publish(initMessage);
            },
            cancellationToken
        );

        var sub2 = await _subscriber.SubscribeAsync(
            IPCMessageType.UPDATE,
            x =>
            {
                var updateMessage = (UpdateMessage)x;
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
                DaemonResult result = await DaemonLoop();

                if (_cts.Token.IsCancellationRequested)
                {
                    break;
                }

                // OS blocked launch (SmartScreen/MOTW) — no point retrying
                if (result.Reason == DaemonExitReason.BlockedByOS)
                {
                    break;
                }

                double uptimeSeconds = (DateTime.Now - then).TotalSeconds;

                // Intentional kill with sufficient uptime — treat as normal, reset fast-fail counter
                if (result.Reason == DaemonExitReason.Killed && uptimeSeconds >= 20)
                {
                    fastFailCount = 0;
                }
                else
                {
                    fastFailCount++;
                    DiagnosticLogger.LogError($"[LGSTrayHIDManager]: HID daemon fast-failed (uptime {uptimeSeconds:F1}s, " +
                                         $"reason {result.Reason}, exit code {result.ExitCode}, count {fastFailCount}/3)");
                }

                if (fastFailCount > 3)
                {
                    DiagnosticLogger.LogError("[LGSTrayHIDManager]: HID daemon exceeded fast-fail limit — giving up.");
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
