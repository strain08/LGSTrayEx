using System;
using System.Diagnostics;
using Winmdroot = Windows.Win32;

namespace LGSTrayUI.Extensions;

public static class AppExtensions
{
    public static unsafe void EnableEfficiencyMode()
    {
        // Efficiency Mode (ProcessPowerThrottling) requires Windows 11 build 22000+
        if (!WindowsVersionHelper.IsWindows11OrGreater)
        {
            LGSTrayPrimitives.DiagnosticLogger.Log("Efficiency Mode not available on this Windows version");
            return;
        }

        try
        {
            var processHandle = Process.GetCurrentProcess().SafeHandle;
            var handle = new Winmdroot.Foundation.HANDLE(processHandle.DangerousGetHandle());
            Winmdroot.PInvoke.SetPriorityClass(handle, Winmdroot.System.Threading.PROCESS_CREATION_FLAGS.IDLE_PRIORITY_CLASS);

            Winmdroot.System.Threading.PROCESS_POWER_THROTTLING_STATE state = new()
            {
                Version = Winmdroot.PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = Winmdroot.PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = Winmdroot.PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            };

#pragma warning disable CA1416 // Platform compatibility - already checked via WindowsVersionHelper.IsWindows11OrGreater
            Winmdroot.PInvoke.SetProcessInformation(
                handle,
                Winmdroot.System.Threading.PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                &state,
                (uint)sizeof(Winmdroot.System.Threading.PROCESS_POWER_THROTTLING_STATE)
            );
#pragma warning restore CA1416

            LGSTrayPrimitives.DiagnosticLogger.Log("Efficiency Mode enabled successfully");
        }
        catch (Exception ex)
        {
            LGSTrayPrimitives.DiagnosticLogger.LogWarning($"Failed to enable Efficiency Mode: {ex.Message}");
        }
    }
}
