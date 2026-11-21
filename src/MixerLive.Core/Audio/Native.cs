using System.Runtime.InteropServices;

namespace MixerLive.Core.Audio;

/// <summary>P/Invoke helpers for multimedia-class scheduling and the 1 ms system timer.</summary>
internal static partial class Native
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    internal static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", ExactSpelling = true)]
    internal static extern uint timeEndPeriod(uint period);

    internal const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    internal const uint TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine,
        IntPtr completionArg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>Registers the calling thread with MMCSS "Pro Audio" so Windows schedules it like other audio engines.</summary>
    internal static IntPtr EnterProAudio()
    {
        uint index = 0;
        try
        {
            return AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    internal static void LeaveProAudio(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { /* ignore */ }
    }
}
