using LGSTrayCore.Managers;
using Xunit;

namespace LGSTrayCore.Tests.Managers;

/// <summary>
/// The daemon restart limiter exists to stop a crash-loop from thrashing, not to punish a
/// daemon that ran for hours and then died once. Issue #27: the HID daemon crashed twice with
/// uptimes of 26841s and 8642s, and both were counted as "fast failures" - four such crashes
/// (possibly days apart) would trip the limit and leave the user with no battery readings
/// until they restarted the app manually.
/// </summary>
public class DaemonRestartPolicyTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(DaemonRestartPolicy.HealthyUptimeSeconds - 0.1)]
    public void ShortLivedExit_CountsAsFastFail(double uptimeSeconds)
    {
        Assert.True(DaemonRestartPolicy.IsFastFail(DaemonExitReason.Normal, uptimeSeconds));
    }

    [Theory]
    [InlineData(DaemonRestartPolicy.HealthyUptimeSeconds)]
    [InlineData(8642.3)]   // issue #27, second crash
    [InlineData(26841.3)]  // issue #27, first crash
    public void CrashAfterHealthyRun_IsNotAFastFail(double uptimeSeconds)
    {
        Assert.False(DaemonRestartPolicy.IsFastFail(DaemonExitReason.Normal, uptimeSeconds));
    }

    [Fact]
    public void CrashAfterHealthyRun_ResetsTheCounter()
    {
        Assert.Equal(0, DaemonRestartPolicy.NextFastFailCount(2, DaemonExitReason.Normal, 26841.3));
    }

    [Fact]
    public void RepeatedCrashesAfterHealthyRuns_NeverGiveUp()
    {
        int fastFailCount = 0;

        // Ten separate long-running sessions, each ending in a crash.
        for (int i = 0; i < 10; i++)
        {
            fastFailCount = DaemonRestartPolicy.NextFastFailCount(fastFailCount, DaemonExitReason.Normal, 8642.3);
            Assert.False(DaemonRestartPolicy.ShouldGiveUp(fastFailCount));
        }
    }

    [Fact]
    public void ConsecutiveCrashLoop_EventuallyGivesUp()
    {
        int fastFailCount = 0;
        int restarts = 0;

        while (!DaemonRestartPolicy.ShouldGiveUp(fastFailCount))
        {
            fastFailCount = DaemonRestartPolicy.NextFastFailCount(fastFailCount, DaemonExitReason.Normal, 1.5);
            restarts++;
            Assert.True(restarts < 100, "crash loop should be bounded");
        }

        Assert.Equal(DaemonRestartPolicy.FastFailLimit + 1, restarts);
    }

    [Fact]
    public void HealthyRunAfterCrashLoop_ClearsAccumulatedCount()
    {
        int fastFailCount = 0;
        fastFailCount = DaemonRestartPolicy.NextFastFailCount(fastFailCount, DaemonExitReason.Normal, 1.0);
        fastFailCount = DaemonRestartPolicy.NextFastFailCount(fastFailCount, DaemonExitReason.Normal, 1.0);
        Assert.Equal(2, fastFailCount);

        fastFailCount = DaemonRestartPolicy.NextFastFailCount(fastFailCount, DaemonExitReason.Normal, 3600.0);
        Assert.Equal(0, fastFailCount);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.1)]
    [InlineData(DaemonRestartPolicy.HealthyUptimeSeconds)]
    public void Rediscover_IsNeverAFastFail(double uptimeSeconds)
    {
        Assert.False(DaemonRestartPolicy.IsFastFail(DaemonExitReason.Rediscover, uptimeSeconds));
    }

    [Fact]
    public void RepeatedRediscover_NeverGivesUp()
    {
        // The user clicking "Detect devices" over and over kills the daemon each time, often
        // within a second or two of it starting. That must never trip the limiter.
        int fastFailCount = 0;

        for (int i = 0; i < 10; i++)
        {
            fastFailCount = DaemonRestartPolicy.NextFastFailCount(fastFailCount, DaemonExitReason.Rediscover, 1.1);
            Assert.False(DaemonRestartPolicy.ShouldGiveUp(fastFailCount));
        }

        Assert.Equal(0, fastFailCount);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3600.0)]
    public void Rediscover_LeavesCountUnchanged(double uptimeSeconds)
    {
        // Rediscovering in the middle of a crash loop neither consumes the budget nor wipes it.
        Assert.Equal(2, DaemonRestartPolicy.NextFastFailCount(2, DaemonExitReason.Rediscover, uptimeSeconds));
    }

    [Fact]
    public void LaunchFailure_CountsAsFastFail()
    {
        Assert.True(DaemonRestartPolicy.IsFastFail(DaemonExitReason.LaunchFailed, 0.0));
    }
}
