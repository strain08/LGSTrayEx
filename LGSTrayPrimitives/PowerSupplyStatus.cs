namespace LGSTrayPrimitives;

public enum PowerSupplyStatus : byte
{
    DISCHARGING = 0,
    CHARGING,
    FULL,
    NOT_CHARGING,
    UNKNOWN
}
