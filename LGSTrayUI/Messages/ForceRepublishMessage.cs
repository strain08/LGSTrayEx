namespace LGSTrayUI.Messages;

/// <summary>
/// Message sent when MQTT service should force republish all device discovery configs.
/// Used when Home Assistant comes online (birth message) to ensure devices re-register.
/// </summary>
public sealed record ForceRepublishMessage;
