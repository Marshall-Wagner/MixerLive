namespace MixerLive.Core.Audio;

/// <summary>Turns Core Audio HRESULTs into messages a user can act on.</summary>
public static class AudioErrors
{
    public const int DeviceInvalidated = unchecked((int)0x88890004);
    public const int UnsupportedFormat = unchecked((int)0x88890008);
    public const int DeviceInUse = unchecked((int)0x8889000A);
    public const int ExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    public const int EndpointCreateFailed = unchecked((int)0x8889000F);
    public const int ServiceNotRunning = unchecked((int)0x88890010);
    public const int ExclusiveModeOnly = unchecked((int)0x88890016);
    public const int CpuUsageExceeded = unchecked((int)0x88890017);
    public const int BufferSizeNotAligned = unchecked((int)0x88890019);
    public const int InvalidDevicePeriod = unchecked((int)0x88890020);
    public const int InvalidStreamFlag = unchecked((int)0x88890021);
    public const int ResourcesInvalidated = unchecked((int)0x88890026);
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int InvalidArg = unchecked((int)0x80070057);

    public static string Describe(Exception ex)
    {
        int hr = ex.HResult;
        string? text = hr switch
        {
            DeviceInUse => "the device is in use by another program in exclusive mode (close Voicemeeter, or turn off 'Allow applications to take exclusive control' in the device's Windows properties)",
            ExclusiveModeNotAllowed => "exclusive mode is disabled for this device in Windows (Sound settings → device → Advanced → 'Allow applications to take exclusive control')",
            ResourcesInvalidated => "another program took the device in exclusive mode",
            DeviceInvalidated => "the device was removed or its settings changed",
            UnsupportedFormat => "the device does not support the requested format",
            EndpointCreateFailed => "Windows could not open the endpoint (device busy or driver problem)",
            ServiceNotRunning => "the Windows Audio service is not running",
            CpuUsageExceeded => "the audio CPU-usage limit was exceeded",
            InvalidDevicePeriod => "the driver rejected the requested buffer period",
            AccessDenied => "access denied – check Settings → Privacy & security → Microphone → 'Let desktop apps access your microphone'",
            _ => null
        };
        return text != null ? $"{text} (0x{hr:X8})" : ex.Message;
    }

    /// <summary>True when retrying with the device's own mix format could help.</summary>
    public static bool IsFormatRelated(Exception ex) => ex.HResult is UnsupportedFormat or InvalidArg
        or InvalidStreamFlag or InvalidDevicePeriod or BufferSizeNotAligned;
}
