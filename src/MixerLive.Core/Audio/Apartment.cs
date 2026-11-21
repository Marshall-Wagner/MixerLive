namespace MixerLive.Core.Audio;

/// <summary>
/// Core Audio COM objects are created on MTA (thread-pool) threads and must only be touched
/// from MTA threads; calling them from the WPF STA thread triggers COM marshaling that fails
/// with E_NOINTERFACE for these interfaces. These helpers hop to the thread pool when needed.
/// </summary>
internal static class Apartment
{
    public static bool IsMta => Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA;

    public static void RunOnMta(Action action)
    {
        if (IsMta) action();
        else Task.Run(action).GetAwaiter().GetResult();
    }

    public static T RunOnMta<T>(Func<T> func)
    {
        return IsMta ? func() : Task.Run(func).GetAwaiter().GetResult();
    }
}
