using System.Runtime.InteropServices;

namespace FlugelKranz.Platform;

/// <summary>
/// Waits on a high-resolution waitable timer (Windows 10 1803 and later). Task.Delay and
/// Thread.Sleep resolve to the 15.6 ms system tick unless the process raises the timer
/// resolution for everything, which this avoids. Falls back to Task.Delay elsewhere.
/// </summary>
internal static class HighResolutionDelay
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint WaitObject0 = 0;
    private static readonly object Gate = new();
    private static IntPtr timer;
    private static bool unavailable;

    /// <summary>Blocks the calling thread for about <paramref name="interval"/>. The command loop owns its thread, so this is cheaper than yielding.</summary>
    public static Task Wait(TimeSpan interval, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.Delay(interval, token);
        lock (Gate)
        {
            if (timer == IntPtr.Zero && !unavailable)
            {
                timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
                unavailable = timer == IntPtr.Zero;
            }
            if (unavailable)
                return Task.Delay(interval, token);
            long due = -Math.Max(1, interval.Ticks); // relative, 100 ns units
            if (!SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false) ||
                WaitForSingleObject(timer, 1000) != WaitObject0)
                return Task.Delay(interval, token);
        }
        return Task.CompletedTask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion,
        IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
