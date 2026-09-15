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

    // The restart loop; replaced by a new one when rediscover finds it has given up.
    private readonly object _supervisorLock = new();
    private Task? _supervisor;

    // Set once the OS has blocked the daemon from launching. Rediscover runs on every system
    // resume, so restarting after a block would re-show the "Windows blocked" message box on
    // each wake; the user has to unblock the files and restart the app instead.
    private volatile bool _blockedByOS;

    private readonly IDistributedSubscriber<IPCMessageType, IPCMessage> _subscriber;
    private readonly IPublisher<IPCMessage> _deviceEventBus;

    public LGSTrayHIDManager(
        IDistributedSubscriber<IPCMessageType, IPCMessage> subscriber,
        IPublisher<IPCMessage> deviceEventBus)
    {
        _subscriber = subscriber;
        _deviceEventBus = deviceEventBus;
    }

    private readonly struct DaemonResult
    {
        public int ExitCode { get; init; }        
        public DaemonExitReason Reason { get; init; }
        
        public static DaemonResult Exited(int exitCode) => new() { 
            ExitCode = exitCode, 
            Reason = DaemonExitReason.Normal 
        };
        public static DaemonResult WasKilled(DaemonExitReason reason) => new() {
            ExitCode = -1,
            Reason = reason
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
        DaemonExitReason killReason = DaemonExitReason.Stopped;
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

                // Must be read here: the finally below disposes and clears _daemonCts.
                if (_daemonCts.IsCancellationRequested)
                {
                    killReason = DaemonExitReason.Rediscover;
                }
            }
        }
        finally
        {
            _daemonCts.Dispose();
            _daemonCts = null;
        }

        DiagnosticLogger.Log($"[LGSTrayHIDManager]: HID daemon exited (exit code {proc.ExitCode})");

        await Task.Delay(1000);
        return wasKilled ? DaemonResult.WasKilled(killReason) : DaemonResult.Exited(proc.ExitCode);
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

        TryStartSupervisor();

        return;
    }

    /// <summary>
    /// Starts the daemon supervisor unless one is already running, the host is stopping, or the
    /// OS has blocked the daemon. The supervisor ends for good when it hits the fast-fail limit,
    /// so this is also how a rediscover brings native HID back afterwards.
    /// </summary>
    /// <returns>true if a new supervisor was started.</returns>
    private bool TryStartSupervisor()
    {
        lock (_supervisorLock)
        {
            if (_cts.IsCancellationRequested)
                return false;

            if (_blockedByOS)
                return false;

            if (_supervisor != null && !_supervisor.IsCompleted)
                return false;

            _supervisor = Task.Run(() => SuperviseDaemon(), CancellationToken.None);
            
            return true;
        }
    }

    private async Task SuperviseDaemon()
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
                _blockedByOS = true;
                break;
            }

            double uptimeSeconds = (DateTime.Now - then).TotalSeconds;
            bool wasFastFail = DaemonRestartPolicy.IsFastFail(result.Reason, uptimeSeconds);
            fastFailCount = DaemonRestartPolicy.NextFastFailCount(fastFailCount, result.Reason, uptimeSeconds);

            if (wasFastFail)
            {
                DiagnosticLogger.LogError($"[LGSTrayHIDManager]: HID daemon fast-failed (uptime {uptimeSeconds:F1}s, " +
                                     $"reason {result.Reason}, exit code {result.ExitCode}, " +
                                     $"count {fastFailCount}/{DaemonRestartPolicy.FastFailLimit})");
            }
            else if (result.Reason != DaemonExitReason.Rediscover)
            {
                // Died on its own after a healthy run: worth reporting, but it is not a
                // crash loop and must not consume the restart budget.
                DiagnosticLogger.LogError($"[LGSTrayHIDManager]: HID daemon exited after a healthy run " +
                                     $"(uptime {uptimeSeconds:F1}s, reason {result.Reason}, exit code {result.ExitCode}) — restarting.");
            }

            if (DaemonRestartPolicy.ShouldGiveUp(fastFailCount))
            {
                DiagnosticLogger.LogError("[LGSTrayHIDManager]: HID daemon exceeded fast-fail limit — giving up.");
                break;
            }

            DiagnosticLogger.Log("[LGSTrayHIDManager]: Restarting HID daemon...");
        }
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

        // Now restart daemon to rediscover devices fresh. If the supervisor has already given up
        // on the fast-fail limit there is no daemon to kill, so start a new supervisor with a
        // fresh restart budget instead.
        if (TryStartSupervisor())
        {
            DiagnosticLogger.Log("[LGSTrayHIDManager]: HID daemon was not running — starting it for rediscovery");
        }
        else if (_blockedByOS)
        {
            DiagnosticLogger.LogWarning("[LGSTrayHIDManager]: HID daemon was blocked by the OS — not restarting. " +
                                        "Unblock the app files and restart the app.");
            return;
        }
        else
        {
            _daemonCts?.Cancel();
        }

        DiagnosticLogger.Log("Native HID device rediscovery initiated (daemon restart)");
    }
}
