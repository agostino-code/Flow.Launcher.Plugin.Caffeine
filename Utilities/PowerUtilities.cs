using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.Caffeine.Utilities;

/// <summary>
/// Utilities for preventing the system from going to sleep
/// </summary>
public static class PowerUtilities
{
    /// <summary>
    /// Execution state flags for preventing power save mode
    /// </summary>
    [Flags]
    public enum EXECUTION_STATE : uint
    {
        /// <summary>
        /// Away mode required
        /// </summary>
        ES_AWAYMODE_REQUIRED = 0x00000040,
        /// <summary>
        /// Continuous execution state
        /// </summary>
        ES_CONTINUOUS = 0x80000000,
        /// <summary>
        /// Display required to stay on
        /// </summary>
        ES_DISPLAY_REQUIRED = 0x00000002,
        /// <summary>
        /// System required to stay awake
        /// </summary>
        ES_SYSTEM_REQUIRED = 0x00000001
        // Legacy flag, should not be used.
        // ES_USER_PRESENT = 0x00000004
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern uint SetThreadExecutionState(EXECUTION_STATE esFlags);

    private static AutoResetEvent _event = new AutoResetEvent(false);

    /// <summary>
    /// Prevent the system from entering power save mode
    /// </summary>
    public static void PreventPowerSave()
    {
        new TaskFactory().StartNew(() =>
            {
                SetThreadExecutionState(
                    EXECUTION_STATE.ES_CONTINUOUS
                    | EXECUTION_STATE.ES_DISPLAY_REQUIRED
                    | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
                _event.WaitOne();

            },
            TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// Allow the system to enter power save mode again
    /// </summary>
    public static void Shutdown()
    {
        _event.Set();
    }
}
