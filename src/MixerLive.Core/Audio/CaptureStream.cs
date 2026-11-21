using MixerLive.Core.Logging;
using MixerLive.Core.Model;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MixerLive.Core.Audio;

/// <summary>
/// WASAPI capture (microphone/line) or loopback (render endpoint) stream.
///
/// Shared mode: Windows is asked to deliver float32 stereo at the engine rate (AUTOCONVERTPCM); if the
/// driver refuses, the stream falls back to the device mix format and converts in software.
/// Exclusive mode (hardware inputs only): the stream owns the device and reads its native PCM
/// format with the smallest buffer the driver allows; the engine resamples if rates differ.
/// Captured frames are pushed into <see cref="Ring"/> from a dedicated MMCSS thread.
/// </summary>
public sealed class CaptureStream : IDisposable
{
    private const long SharedBufferDuration100ns = 60 * 10_000; // 60 ms of slack

    private readonly MMDevice _device;
    private readonly int _engineRate;
    private AudioClient? _client;
    private AudioCaptureClient? _capture;
    private EventWaitHandle? _event;
    private Thread? _thread;
    private float[] _tmp = Array.Empty<float>();
    private SampleKind _kind;
    private long _frames;
    private volatile bool _stop;
    private volatile bool _faulted;

    public FloatRing Ring { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsLoopback { get; }
    public bool Exclusive { get; }
    public LatencyPreset Latency { get; }
    public int? ExclusiveBufferMs { get; }
    public WaveFormat? Format { get; private set; }
    public WaveFormat? MixFormat { get; private set; }
    public int ValidBits { get; private set; }
    public double PeriodMs { get; private set; }
    public int SourceRate { get; private set; }
    public int SourceChannels { get; private set; }
    public bool SoftwareConvert { get; private set; }
    public long FramesCaptured => Volatile.Read(ref _frames);
    public bool Faulted => _faulted;
    public string? FaultMessage { get; private set; }

    public event Action<CaptureStream>? Failed;

    /// <param name="exclusiveBufferMs">Advanced override of the exclusive-mode driver buffer; null = from the preset.</param>
    public CaptureStream(MMDevice device, bool loopback, int engineRate, int ringFrames, bool exclusive, LatencyPreset latency, int? exclusiveBufferMs = null)
    {
        _device = device;
        _engineRate = engineRate;
        IsLoopback = loopback;
        Exclusive = exclusive && !loopback; // loopback capture only exists in shared mode
        Latency = latency;
        ExclusiveBufferMs = exclusiveBufferMs;
        DeviceId = device.ID;
        DeviceName = SafeName(device);
        Ring = new FloatRing(2, ringFrames);
    }

    public string ModeDescription
    {
        get
        {
            if (Format == null) return "";
            if (Exclusive)
            {
                string conv = SourceRate != _engineRate ? $", resampled to {WaveFormats.RateText(_engineRate)}" : "";
                return $"exclusive · {WaveFormats.Describe(Format, ValidBits)} · {PeriodMs:0.#} ms buffer{conv}";
            }
            string mix = MixFormat != null ? WaveFormats.Describe(MixFormat, MixFormat.BitsPerSample) : "?";
            string sw = SoftwareConvert ? " (software conversion)" : "";
            return $"{(IsLoopback ? "loopback" : "shared")} · Windows runs it at {mix}{sw}";
        }
    }

    public void Open()
    {
        if (Exclusive) OpenExclusive();
        else OpenShared();

        _kind = SampleConvert.Detect(Format!);
        if (_kind == SampleKind.Unsupported)
            throw new NotSupportedException($"Unsupported capture format: {Format}");

        SourceRate = Format!.SampleRate;
        SourceChannels = Format.Channels;

        _event = new EventWaitHandle(false, EventResetMode.AutoReset);
        _client!.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient;
        _tmp = new float[Math.Max(4096, _client.BufferSize * 2 + 1024)];
    }

    private void OpenShared()
    {
        var want = WaveFormat.CreateIeeeFloatWaveFormat(_engineRate, 2);
        var baseFlags = AudioClientStreamFlags.EventCallback |
                        (IsLoopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None);
        try
        {
            _client = _device.AudioClient;
            MixFormat = _client.MixFormat;
            _client.Initialize(AudioClientShareMode.Shared,
                baseFlags | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                SharedBufferDuration100ns, 0, want, Guid.Empty);
            Format = want;
            SoftwareConvert = false;
        }
        catch (Exception ex) when (AudioErrors.IsFormatRelated(ex))
        {
            Log.Warn($"Capture '{DeviceName}': auto-convert init failed ({ex.Message}); using device mix format");
            try { _client?.Dispose(); } catch { /* ignore */ }
            _client = _device.AudioClient;
            var mix = _client.MixFormat;
            MixFormat = mix;
            _client.Initialize(AudioClientShareMode.Shared, baseFlags, SharedBufferDuration100ns, 0, mix, Guid.Empty);
            Format = mix;
            SoftwareConvert = true;
        }
        ValidBits = Format.BitsPerSample;
        PeriodMs = 10;
    }

    private void OpenExclusive()
    {
        var fmt = ExclusiveMode.ProbeFormat(_device, _engineRate, out int validBits, out string tried)
                  ?? throw new NotSupportedException($"the driver accepted no exclusive-mode PCM format (tried {tried})");

        long period = ExclusiveMode.Initialize(_device, fmt, Latency, ExclusiveBufferMs, AudioClientStreamFlags.None, out _client);
        Format = fmt;
        ValidBits = validBits;
        PeriodMs = period / 10_000.0;
        SoftwareConvert = fmt.SampleRate != _engineRate;
    }

    public void Start()
    {
        if (_client == null) throw new InvalidOperationException("Open() first");
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"capture:{DeviceName}",
            Priority = ThreadPriority.Highest
        };
        _thread.Start();
    }

    private void Run()
    {
        IntPtr mm = Native.EnterProAudio();
        try
        {
            _client!.Start();
            while (!_stop)
            {
                // Timeout keeps loopback streams alive even when Windows stops signalling
                // the event because nothing is rendering to the endpoint.
                _event!.WaitOne(20);
                if (_stop) break;

                while (!_stop && _capture!.GetNextPacketSize() > 0)
                {
                    IntPtr ptr = _capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    if (frames > 0)
                    {
                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            Ring.WriteSilence(frames);
                        }
                        else
                        {
                            if (_tmp.Length < frames * 2) _tmp = new float[frames * 2];
                            SampleConvert.ToStereoFloat(ptr, _kind, SourceChannels, frames, _tmp);
                            Ring.Write(_tmp, frames);
                        }
                        Interlocked.Add(ref _frames, frames);
                    }
                    _capture.ReleaseBuffer(frames);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stop)
            {
                FaultMessage = AudioErrors.Describe(ex);
                _faulted = true;
                Log.Warn($"Capture '{DeviceName}' stopped: {FaultMessage}");
                try { Failed?.Invoke(this); } catch { /* ignore */ }
            }
        }
        finally
        {
            try { _client?.Stop(); } catch { /* ignore */ }
            Native.LeaveProAudio(mm);
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _event?.Set(); } catch { /* ignore */ }
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(1500);
        try { _capture?.Dispose(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        try { _event?.Dispose(); } catch { /* ignore */ }
        try { _device.Dispose(); } catch { /* ignore */ }
    }

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; } catch { return d.ID; }
    }
}
