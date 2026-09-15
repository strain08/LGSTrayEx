namespace LGSTrayCore.Managers;

public enum DaemonExitReason
{
    Normal,       // Process exited on its own
    Rediscover,   // We killed the process because the user asked for rediscovery — not a failure
    Stopped,      // We killed the process on host shutdown (loop exits first), or waiting failed unexpectedly (judged by uptime)
    LaunchFailed, // Failed to start — retriable
    BlockedByOS,  // OS blocked launch (SmartScreen/MOTW) — permanent, give up
}

/// <summary>
/// Decides whether a HID daemon exit means "it cannot start, stop thrashing" or
/// "it ran fine and then died once, just restart it".
///
/// For exits we did not cause, the distinction is uptime: a daemon that crashes after hours of
/// healthy operation is not crash-looping, and must not consume the restart budget.
/// Exits we caused ourselves (rediscover) say nothing about daemon health and never count.
/// </summary>
public static class DaemonRestartPolicy
{
    public const double HealthyUptimeSeconds = 60;

    /// <summary>Consecutive fast failures tolerated before giving up.</summary>
    public const int FastFailLimit = 3;
    /// <summary>
    /// True if this exit is considered a "fast failure" that counts against the restart budget.
    /// </summary>
    /// <param name="reason"></param>
    /// <param name="uptimeSeconds"></param>
    /// <returns></returns>
    public static bool IsFastFail(DaemonExitReason reason, double uptimeSeconds)
    {
        if (reason == DaemonExitReason.Rediscover)
        {
            return false; // we killed it (rediscover)? not a failure
        }

        return uptimeSeconds < HealthyUptimeSeconds; // exited in 50 seconds by any reason? that's a fast fail.
    }

    /// <summary>
    /// The consecutive fast-failure count after this exit. 
    /// </summary>
    public static int NextFastFailCount(int currentCount, DaemonExitReason reason, double uptimeSeconds)
    {
        if (reason == DaemonExitReason.Rediscover)
        {
            return currentCount; // we killed it (rediscover)? not a failure, don't increment the count
        }

        if (IsFastFail(reason, uptimeSeconds))
        {
            return currentCount + 1; // exited too quickly, increment the count
        }

        return 0; // reset when exit normally
    }

    public static bool ShouldGiveUp(int fastFailCount) => fastFailCount > FastFailLimit;
}
