using System.Runtime.InteropServices;
using MixerLive.Core.Model;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MixerLive.Core.Audio;

/// <summary>Builds the exact WAVEFORMATEXTENSIBLE layouts that drivers expect in exclusive mode.</summary>
public static class WaveFormats
{
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// PCM in an extensible header with an explicit valid-bit count (e.g. 24-in-32).
    /// NAudio's own constructor flags every 32-bit format as IEEE float, which DAC drivers reject,
    /// so the structure is laid out by hand and read back through NAudio's marshaller.
    /// </summary>
    public static WaveFormat PcmExtensible(int sampleRate, int channels, int containerBits, int validBits)
    {
        int blockAlign = channels * containerBits / 8;
        IntPtr p = Marshal.AllocHGlobal(40);
        try
        {
            Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));          // WAVE_FORMAT_EXTENSIBLE
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, sampleRate);
            Marshal.WriteInt32(p, 8, sampleRate * blockAlign);
            Marshal.WriteInt16(p, 12, (short)blockAlign);
            Marshal.WriteInt16(p, 14, (short)containerBits);
            Marshal.WriteInt16(p, 16, 22);                                // cbSize
            Marshal.WriteInt16(p, 18, (short)validBits);
            Marshal.WriteInt32(p, 20, channels == 1 ? 0x4 : 0x3);         // SPEAKER_FRONT_CENTER / FRONT_LEFT|RIGHT
            Marshal.Copy(SubtypePcm.ToByteArray(), 0, p + 24, 16);
            return WaveFormat.MarshalFromPtr(p);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    public static bool IsFloat(WaveFormat f) =>
        f is WaveFormatExtensible ext ? ext.SubFormat == SubtypeFloat : f.Encoding == WaveFormatEncoding.IeeeFloat;

    /// <summary>"32-bit PCM 768 kHz", "24-in-32-bit PCM 192 kHz", "32-bit float 384 kHz", "16-bit PCM 48 kHz mono".</summary>
    public static string Describe(WaveFormat f, int validBits)
    {
        string bits = IsFloat(f) ? $"{f.BitsPerSample}-bit float"
            : validBits > 0 && validBits < f.BitsPerSample ? $"{validBits}-in-{f.BitsPerSample}-bit PCM"
            : $"{f.BitsPerSample}-bit PCM";
        string ch = f.Channels == 1 ? " mono" : f.Channels > 2 ? $" {f.Channels}ch" : "";
        return $"{bits} {RateText(f.SampleRate)}{ch}";
    }

    public static string RateText(int hz) => hz % 1000 == 0 ? $"{hz / 1000} kHz" : $"{hz / 1000.0:0.#} kHz";
}

/// <summary>Shared logic for opening a WASAPI stream in exclusive mode.</summary>
internal static class ExclusiveMode
{
    private static readonly int[] FallbackRates =
    {
        768000, 705600, 384000, 352800, 192000, 176400, 96000, 88200, 48000, 44100,
        32000, 24000, 22050, 16000, 11025, 8000   // webcams and headsets
    };
    private static readonly (int Container, int Valid)[] Depths = { (32, 32), (32, 24), (24, 24), (16, 16) };

    /// <summary>
    /// Finds the first PCM format the driver accepts, preferring the engine rate and the widest
    /// sample. Returns null when the device refuses every candidate.
    /// </summary>
    public static WaveFormat? ProbeFormat(MMDevice device, int engineRate, out int validBits, out string tried)
    {
        var rates = new List<int> { engineRate };
        foreach (int r in FallbackRates) if (!rates.Contains(r)) rates.Add(r);

        var attempted = new List<string>();
        foreach (int rate in rates)
        {
            foreach (var (container, valid) in Depths)
            {
                // Stereo first; mono for microphones and other single-channel devices.
                foreach (int channels in new[] { 2, 1 })
                {
                    var fmt = WaveFormats.PcmExtensible(rate, channels, container, valid);
                    bool ok;
                    try
                    {
                        using var probe = device.AudioClient;
                        ok = probe.IsFormatSupported(AudioClientShareMode.Exclusive, fmt);
                    }
                    catch
                    {
                        ok = false;
                    }
                    if (ok)
                    {
                        validBits = valid;
                        tried = string.Join(", ", attempted);
                        return fmt;
                    }
                    if (channels == 2 && attempted.Count < 6) attempted.Add(WaveFormats.Describe(fmt, valid));
                }
            }
        }
        validBits = 0;
        tried = string.Join(", ", attempted) + " …";
        return null;
    }

    /// <summary>
    /// Initialises an event-driven exclusive stream, handling the buffer-alignment and
    /// device-period retries that WASAPI requires. Returns the period in 100 ns units.
    /// <paramref name="bufferMs"/> (Advanced settings) replaces the preset's choice; the driver's
    /// minimum period still applies if it is larger.
    /// </summary>
    public static long Initialize(MMDevice device, WaveFormat format, LatencyPreset latency, int? bufferMs, AudioClientStreamFlags extraFlags, out AudioClient client)
    {
        long defPeriod, minPeriod;
        using (var c = device.AudioClient)
        {
            defPeriod = c.DefaultDevicePeriod;
            minPeriod = c.MinimumDevicePeriod;
        }
        if (defPeriod <= 0) defPeriod = 100_000;
        if (minPeriod <= 0) minPeriod = 30_000;

        // Override: as asked (the driver's minimum still wins). Presets: Low = the driver's minimum,
        // Normal = its default, Safe = twice the default, each at least EngineOptions.PresetExclusiveBufferMs.
        long floor = (bufferMs ?? EngineOptions.PresetExclusiveBufferMs(latency)) * 10_000L;
        long period = bufferMs != null ? Math.Max(minPeriod, floor) : latency switch
        {
            LatencyPreset.Low => Math.Max(minPeriod, floor),
            LatencyPreset.Safe => Math.Max(defPeriod * 2, floor),
            _ => Math.Max(defPeriod, floor)
        };

        var flags = AudioClientStreamFlags.EventCallback | extraFlags;
        for (int attempt = 0; ; attempt++)
        {
            client = device.AudioClient;
            try
            {
                client.Initialize(AudioClientShareMode.Exclusive, flags, period, period, format, Guid.Empty);
                return period;
            }
            catch (Exception ex) when (attempt < 3 && ex.HResult == AudioErrors.BufferSizeNotAligned)
            {
                int frames;
                try { frames = client.BufferSize; }
                catch { frames = (int)Math.Ceiling(period * format.SampleRate / 10_000_000.0 / 32.0) * 32; }
                long aligned = (long)Math.Round(10_000_000.0 * frames / format.SampleRate);
                period = aligned == period ? aligned + 10 : aligned;
                client.Dispose();
            }
            catch (Exception ex) when (attempt < 3 && ex.HResult == AudioErrors.InvalidDevicePeriod && period != defPeriod)
            {
                period = defPeriod;
                client.Dispose();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }
}
