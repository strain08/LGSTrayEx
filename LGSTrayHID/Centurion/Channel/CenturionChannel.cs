using LGSTrayHID.Centurion.Transport;
using LGSTrayPrimitives;

namespace LGSTrayHID.Centurion.Channel;

public delegate Task<CenturionResponse?> CenturionSendAsync(byte featIdx, byte func, byte[] parameters, int timeoutMs = 2000);

/// <summary>
/// Abstract base for Centurion request/response channels.
/// Owns IoLock, PendingRequest, and the core send+wait implementation.
/// </summary>
public abstract class CenturionChannel : IDisposable
{
    protected readonly CenturionTransport Transport;
    protected readonly SemaphoreSlim IoLock = new(1, 1);
    protected readonly CancellationToken Ct;

    /// <summary>
    /// Bundles the TCS with the expected response key so the dispatcher can reject stale responses
    /// from timed-out requests. Must be a class so Interlocked.Exchange works on the reference.
    /// </summary>
    protected sealed class PendingRequest(byte featIdx,
                                          byte funcId,
                                          TaskCompletionSource<CenturionResponse> tcs)
    {
        public byte FeatIdx => featIdx;
        public byte FuncId => funcId;
        public TaskCompletionSource<CenturionResponse> Tcs => tcs;
        public bool MatchesResponse(CenturionResponse resp) =>
            resp.FeatIdx == FeatIdx && resp.FuncId == FuncId;
    }

    protected PendingRequest? _pendingRequest;

    protected CenturionChannel(CenturionTransport transport, CancellationToken ct)
    {
        Transport = transport;
        Ct = ct;
    }
    /// <summary>
    /// Send feature request over
    /// </summary>    
    public abstract Task<CenturionResponse?> SendAsync(byte featIdx,
                                                       byte func,
                                                       byte[] parameters,
                                                       int timeoutMs = 2000);

    /// <summary>
    /// Called by the dispatcher for SwId==SWID frames.
    /// Returns true if the frame was consumed (ACK) or completed a pending request.
    /// Returns false if no pending was found (allows dispatcher to try another channel).
    /// </summary>
    public abstract bool TryCompleteRequest(CenturionResponse frame);

    /// <summary>
    /// Called by the dispatcher for SwId==0x00 frames.
    /// Returns the frame to forward to HandleAsyncEvent, or null to consume it entirely.
    ///   non-null → forward that frame (the original, or an unwrapped bridge sub-event)
    ///   null     → consumed (bridge ACK or solicited sub-response)
    /// Default: pass the frame through unchanged.
    /// </summary>
    public virtual CenturionResponse? RouteEvent(CenturionResponse frame) => frame;

    protected async Task<CenturionResponse?> SendCoreAsync(byte featIdx,
                                                           byte func,
                                                           int timeoutMs,
                                                           Func<Task> sendAction)
    {
        var pending = new PendingRequest(featIdx, func,
            new TaskCompletionSource<CenturionResponse>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await IoLock.WaitAsync(Ct);
            try
            {
                // Register inside the lock so concurrent callers cannot overwrite each other.
                _pendingRequest = pending;
                await sendAction();
            }
            finally
            {
                IoLock.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeoutCts.CancelAfter(timeoutMs);
            return await pending.Tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            DiagnosticLogger.LogWarning($"{Transport.Tag} Request timed out after {timeoutMs}ms (feat=0x{featIdx:X2} func={func})");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _pendingRequest, null);
        }
    }

    /// <summary>
    /// Called by the dispatcher for error frames (feat=0xFF, swid=0).
    /// Cancels the pending request so SendCoreAsync returns null immediately.
    /// Returns false if no pending request exists (allows dispatcher to try another channel).
    /// </summary>
    public bool TryCompleteError(CenturionResponse frame)
    {
        var pending = Interlocked.Exchange(ref _pendingRequest, null);
        if (pending == null)
            return false;
        pending.Tcs.TrySetCanceled();
        return true;
    }

    /// <summary>
    /// Atomically exchange out _pendingRequest and complete it if it matches the frame.
    /// Returns false if there was no pending request (allows caller to try another channel).
    /// </summary>
    protected bool TrySetPending(CenturionResponse frame)
    {
        var pending = Interlocked.Exchange(ref _pendingRequest, null);
        if (pending == null)
        {
            DiagnosticLogger.Verbose($"{Transport.Tag} Late response dropped (no pending): feat=0x{frame.FeatIdx:X2} func={frame.FuncId} swid=0x{frame.SwId:X2} — device may have woken after timeout");
            return false;
        }
        if (pending.MatchesResponse(frame))
        {
            pending.Tcs.TrySetResult(frame);
            return true;
        }
        DiagnosticLogger.LogWarning($"{Transport.Tag} stale/mismatched response dropped " +
            $"(expected feat=0x{pending.FeatIdx:X2} func={pending.FuncId}, " +
            $"got feat=0x{frame.FeatIdx:X2} func={frame.FuncId})");
        return false;
    }

    public virtual void Dispose()
    {
        IoLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
