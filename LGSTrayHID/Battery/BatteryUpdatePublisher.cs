using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayHID.Features;

namespace LGSTrayHID.Battery;

/// <summary>
/// Handles battery state changes and publishes IPC messages.
/// Used by both polling and event-driven update paths to eliminate duplication.
/// </summary>
public class BatteryUpdatePublisher
{
    private BatteryUpdateReturn _lastState;

    /// <summary>
    /// Publishes a battery update if the state has changed or if forced.
    /// Handles deduplication, IPC publishing, and state tracking.
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <param name="deviceName">Device name (for logging)</param>
    /// <param name="newState">New battery state</param>
    /// <param name="updateTime">Timestamp of the update</param>
    /// <param name="source">Source of update ("poll" or "event") for logging</param>
    /// <param name="forceUpdate">Force IPC update even if state unchanged</param>
    /// <returns>True if IPC message was published, false if deduplicated</returns>
    public bool PublishUpdate(
        string deviceId,
        string deviceName,
        BatteryUpdateReturn newState,
        DateTimeOffset updateTime,
        string source,
        bool forceUpdate = false)
    {
        // Log the update with source prefix
        DiagnosticLogger.Log($"[{deviceName}] Battery {source}: {newState.batteryPercentage}%");

        // Check for state change (deduplication)
        // BUG FIX: Original code had `if (forceUpdate || (newState == _lastState))` which was backwards
        // Correct logic: only skip update if NOT forced AND state unchanged
        if (!forceUpdate && newState == _lastState)
        {
            // State unchanged, don't send IPC update
            return false;
        }

        // State changed or forced - send IPC update
        _lastState = newState;
        HidppManagerContext.Instance.SignalDeviceEvent(
            IPCMessageType.UPDATE,
            new UpdateMessage(deviceId, newState.batteryPercentage, newState.status, newState.batteryMVolt, updateTime)
        );

        return true;
    }

    /// <summary>
    /// Gets the last published battery state (for testing or inspection).
    /// </summary>
    public BatteryUpdateReturn LastState => _lastState;
}
