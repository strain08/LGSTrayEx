namespace LGSTrayHID.Battery;

/// <summary>
/// Prevents battery event spam by throttling events based on time intervals.
/// Some devices send rapid bursts of battery events which need to be throttled.
/// </summary>
public class BatteryEventThrottler
{
    private DateTimeOffset _lastEventTime = DateTimeOffset.MinValue;
    private readonly int _throttleMilliseconds;

    /// <summary>
    /// Creates a new battery event throttler with the specified throttle window.
    /// </summary>
    /// <param name="throttleMilliseconds">Minimum time in milliseconds between events (default 500ms)</param>
    public BatteryEventThrottler(int throttleMilliseconds = 500)
    {
        _throttleMilliseconds = throttleMilliseconds;
    }

    /// <summary>
    /// Checks if an event should be processed based on the throttle window.
    /// If the event should be processed, updates the last event time.
    /// </summary>
    /// <param name="now">Current timestamp</param>
    /// <returns>True if the event should be processed, false if throttled</returns>
    public bool ShouldProcessEvent(DateTimeOffset now)
    {
        // Check if enough time has passed since last event
        if ((now - _lastEventTime).TotalMilliseconds < _throttleMilliseconds)
        {
            return false; // Throttled
        }

        // Update last event time and allow processing
        _lastEventTime = now;
        return true;
    }

    /// <summary>
    /// Resets the throttler state (useful for testing or device reconnection).
    /// </summary>
    public void Reset()
    {
        _lastEventTime = DateTimeOffset.MinValue;
    }
}
