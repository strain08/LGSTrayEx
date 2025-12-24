using System;
using System.Collections.Generic;
using System.Text;

namespace LGSTrayHID.Protocol;

internal static class AppConstants
{
    public const byte SW_ID = 0x0A;

    public const int INIT_PING_TIMEOUT_MS = 5000;
    public const int WRITE_READ_TIMEOUT_MS = 5000;
    
    // Battery query timeouts
    public const int UNIFIED_QueryTimeout = 5000;
    public const int UNIFIED_LEVEL_QueryTimeout = 5000;
    public const int BATT_VOLTAGE_QueryTimeout = 5000;

}
