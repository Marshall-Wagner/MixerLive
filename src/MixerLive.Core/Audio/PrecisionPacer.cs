namespace MixerLive.Core.Audio;

/// <summary>
/// Sleeps for sub-millisecond-accurate intervals without busy-waiting.
///
/// Uses a high-resolution waitable timer (Windows 10 1803+), which is precise to roughly 0.5 ms,
/// does not depend on the process timer resolution (which Windows 11 may ignore for windows that
/// are hidden in the tray), and does not raise the system-wide timer interrupt rate the way
/// timeBeginPeriod(1) does. Falls back to Sleep with a 1 ms timer resolution on older systems.
/// </summary>
internal sealed class PrecisionPacer : IDisposable
{
    private IntPtr _timer;
    private readonly bool _raisedTimerResolution;

    public bool HighResolution => _timer != IntPtr.Zero;

    public PrecisionPacer()
    {
        try
        {
            _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null,
                Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        }
        catch
        {
            _timer = IntPtr.Zero;
        }

        if (_timer == IntPtr.Zero)
        {
            Native.timeBeginPeriod(1);
            _raisedTimerResolution = true;
        }
    }

    /// <summary>Blocks the calling thread for about <paramref name="milliseconds"/>.</summary>
    public void Wait(double milliseconds)
    {
        if (milliseconds <= 0) return;

        if (_timer != IntPtr.Zero)
        {
            long due = -Math.Max(1, (long)(milliseconds * 10_000)); // negative = relative, 100 ns units
            if (Native.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                Native.WaitForSingleObject(_timer, 1000);
                return;
            }
        }

        Thread.Sleep(Math.Max(1, (int)Math.Round(milliseconds)));
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero)
        {
            Native.CloseHandle(_timer);
            _timer = IntPtr.Zero;
        }
        if (_raisedTimerResolution) Native.timeEndPeriod(1);
    }
}
