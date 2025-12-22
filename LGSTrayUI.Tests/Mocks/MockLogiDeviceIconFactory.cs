using LGSTrayCore;
using LGSTrayUI.Interfaces;
using System;

namespace LGSTrayUI.Tests.Mocks;

/// <summary>
/// Mock icon factory that doesn't create WPF controls, avoiding STA thread requirement in tests
/// </summary>
public class MockLogiDeviceIconFactory : ILogiDeviceIconFactory
{
    /// <summary>
    /// Returns null instead of creating real WPF control
    /// Tests don't need actual icon rendering
    /// </summary>
    public LogiDeviceIcon? CreateDeviceIcon(LogiDevice device, Action<LogiDeviceIcon>? config = null)
    {
        // Return null - tests verify behavior, not UI rendering
        return null;
    }
}
