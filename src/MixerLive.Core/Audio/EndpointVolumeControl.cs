using MixerLive.Core.Logging;
using MixerLive.Core.Model;
using NAudio.CoreAudioApi;

namespace MixerLive.Core.Audio;

/// <summary>
/// The Windows volume of one playback endpoint, i.e. the device volume shown in Sound settings.
/// On devices with hardware volume (most USB DACs, including the FiiO KA15) this is the DAC's own
/// volume, so it keeps working while MixerLive holds the device in exclusive mode. On devices
/// without it, Windows applies the volume in software, which exclusive streams bypass.
/// All COM access happens on MTA threads.
/// </summary>
public sealed class EndpointVolumeControl : IDisposable
{
    private readonly MMDevice _device;
    private readonly AudioEndpointVolume _volume;
    private bool _disposed;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool HardwareVolume { get; }

    /// <summary>Raised on a COM thread with the new volume in % whenever any program, or the device's own buttons, changes it.</summary>
    public event Action<double>? Changed;

    private EndpointVolumeControl(MMDevice device)
    {
        _device = device;
        DeviceId = device.ID;
        try { DeviceName = device.FriendlyName; } catch { DeviceName = device.ID; }
        _volume = device.AudioEndpointVolume;
        HardwareVolume = (_volume.HardwareSupport & EEndpointHardwareSupport.Volume) != 0;
        _volume.OnVolumeNotification += OnNotify;
    }

    /// <summary>Opens the volume control of a playback device, or returns null when it is not available.</summary>
    public static EndpointVolumeControl? Open(DeviceService devices, DeviceRef device)
    {
        if (device.IsEmpty) return null;
        return Apartment.RunOnMta(() =>
        {
            MMDevice? dev = null;
            try
            {
                dev = devices.Resolve(device.Clone(), DataFlow.Render);
                return dev == null ? null : new EndpointVolumeControl(dev);
            }
            catch (Exception ex)
            {
                Log.Warn($"Device volume unavailable for '{device}': {ex.Message}");
                dev?.Dispose();
                return null;
            }
        });
    }

    /// <summary>Device volume in % (Windows' own scale, identical to the Sound settings slider).</summary>
    public double Percent
    {
        get => Apartment.RunOnMta(() => _disposed ? 0 : _volume.MasterVolumeLevelScalar * 100.0);
        set => Apartment.RunOnMta(() =>
        {
            if (!_disposed) _volume.MasterVolumeLevelScalar = (float)Math.Clamp(value / 100.0, 0, 1);
        });
    }

    private void OnNotify(AudioVolumeNotificationData data)
    {
        try { Changed?.Invoke(data.MasterVolume * 100.0); }
        catch { /* never let a UI handler break the COM callback */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Apartment.RunOnMta(() =>
        {
            try { _volume.OnVolumeNotification -= OnNotify; } catch { /* ignore */ }
            try { _device.Dispose(); } catch { /* ignore */ }
        });
    }
}
