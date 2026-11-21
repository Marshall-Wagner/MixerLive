using MixerLive.Core.Logging;
using MixerLive.Core.Model;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MixerLive.Core.Audio;

/// <summary>
/// WASAPI render stream. The engine writes float32 stereo at the engine rate into <see cref="Ring"/>;
/// a dedicated MMCSS thread pulls it through an <see cref="AdaptiveReader"/> (drift correction and,
/// if needed, rate conversion) into the device buffer whenever Windows signals that there is room.
///
/// Shared mode: Windows mixes and converts to the endpoint's configured format; other apps and
/// APO effects (e.g. Equalizer APO) keep working on the device.
/// Exclusive mode: the stream owns the device and writes the driver's native PCM format directly
/// (e.g. 32-bit 768 kHz on a hi-res DAC), with the smallest buffer the driver allows.
/// </summary>
public sealed class RenderStream : IDisposable
{
    private readonly MMDevice _device;
    private readonly int _engineRate;
    private readonly int _sharedBufferMs;
    private readonly int _targetMs;
    private AudioClient? _client;
    private AudioRenderClient? _render;
    private EventWaitHandle? _event;
    private Thread? _thread;
    private float[] _tmp = Array.Empty<float>();
    private SampleKind _kind;
    private long _frames;
    private volatile bool _stop;
    private volatile bool _faulted;

    public FloatRing Ring { get; }
    public AdaptiveReader Reader { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool Exclusive { get; }
    public LatencyPreset Latency { get; }
    public int? ExclusiveBufferMs { get; }
    public WaveFormat? Format { get; private set; }
    public WaveFormat? MixFormat { get; private set; }
    public int ValidBits { get; private set; }
    public double PeriodMs { get; private set; }
    public int DeviceRate { get; private set; }
    public int DeviceChannels { get; private set; }
    public int BufferFrames { get; private set; }
    public bool SoftwareConvert { get; private set; }
    public long FramesRendered => Volatile.Read(ref _frames);
    public bool Faulted => _faulted;
    public string? FaultMessage { get; private set; }

    public event Action<RenderStream>? Failed;

    /// <param name="exclusiveBufferMs">Advanced override of the exclusive-mode driver buffer; null = from the preset.</param>
    public RenderStream(MMDevice device, int engineRate, int ringFrames, bool exclusive, LatencyPreset latency, int sharedBufferMs, int targetMs, int? exclusiveBufferMs = null)
    {
        _device = device;
        _engineRate = engineRate;
        _sharedBufferMs = Math.Max(10, sharedBufferMs);
        _targetMs = Math.Max(5, targetMs);
        Exclusive = exclusive;
        Latency = latency;
        ExclusiveBufferMs = exclusiveBufferMs;
        DeviceId = device.ID;
        DeviceName = SafeName(device);
        Ring = new FloatRing(2, ringFrames);
        Reader = new AdaptiveReader(Ring);
    }

    /// <summary>Human-readable mode summary for the UI.</summary>
    public string ModeDescription
    {
        get
        {
            if (Format == null) return "";
            if (Exclusive)
            {
                string conv = SoftwareConvert ? $", resampled from {WaveFormats.RateText(_engineRate)}" : "";
                return $"exclusive · {WaveFormats.Describe(Format, ValidBits)} · {PeriodMs:0.#} ms buffer{conv}";
            }
            string mix = MixFormat != null ? WaveFormats.Describe(MixFormat, MixFormat.BitsPerSample) : "?";
            string sw = SoftwareConvert ? " (software conversion)" : "";
            return $"shared · Windows runs it at {mix}{sw}";
        }
    }

    public void Open()
    {
        if (Exclusive) OpenExclusive();
        else OpenShared();

        _kind = SampleConvert.Detect(Format!);
        if (_kind == SampleKind.Unsupported)
            throw new NotSupportedException($"Unsupported render format: {Format}");

        DeviceRate = Format!.SampleRate;
        DeviceChannels = Format.Channels;
        BufferFrames = _client!.BufferSize;

        Reader.NominalRatio = _engineRate / (double)DeviceRate;
        Reader.SourceRate = _engineRate;
        Reader.TargetFill = (int)(_targetMs * _engineRate / 1000.0);
        Reader.Reset();

        _event = new EventWaitHandle(false, EventResetMode.AutoReset);
        _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
        _render = _client.AudioRenderClient;
        _tmp = new float[BufferFrames * 2 + 1024];

        // Pre-roll with silence so the first callback has a full period of headroom.
        _render.GetBuffer(BufferFrames);
        _render.ReleaseBuffer(BufferFrames, AudioClientBufferFlags.Silent);
    }

    private void OpenShared()
    {
        var want = WaveFormat.CreateIeeeFloatWaveFormat(_engineRate, 2);
        long duration = _sharedBufferMs * 10_000L;
        try
        {
            _client = _device.AudioClient;
            MixFormat = _client.MixFormat;
            _client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                duration, 0, want, Guid.Empty);
            Format = want;
            SoftwareConvert = false;
        }
        catch (Exception ex) when (AudioErrors.IsFormatRelated(ex))
        {
            Log.Warn($"Render '{DeviceName}': auto-convert init failed ({ex.Message}); using device mix format");
            try { _client?.Dispose(); } catch { /* ignore */ }
            _client = _device.AudioClient;
            var mix = _client.MixFormat;
            MixFormat = mix;
            _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, duration, 0, mix, Guid.Empty);
            Format = mix;
            SoftwareConvert = true;
        }
        ValidBits = Format.BitsPerSample;
        PeriodMs = _sharedBufferMs;
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
            Name = $"render:{DeviceName}",
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
                bool signaled = _event!.WaitOne(Exclusive ? 250 : 20);
                if (_stop) break;

                int frames;
                if (Exclusive)
                {
                    // Event-driven exclusive streams hand us the whole buffer once per period.
                    if (!signaled) continue;
                    frames = BufferFrames;
                }
                else
                {
                    int padding = _client.CurrentPadding;
                    frames = BufferFrames - padding;
                    if (frames <= 0) continue;
                }

                bool got = Reader.Read(_tmp, frames);
                IntPtr ptr = _render!.GetBuffer(frames);
                if (!got)
                {
                    _render.ReleaseBuffer(frames, AudioClientBufferFlags.Silent);
                }
                else
                {
                    SampleConvert.FromStereoFloat(_tmp, frames, ptr, _kind, DeviceChannels);
                    _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
                }
                Interlocked.Add(ref _frames, frames);
            }
        }
        catch (Exception ex)
        {
            if (!_stop)
            {
                FaultMessage = AudioErrors.Describe(ex);
                _faulted = true;
                Log.Warn($"Render '{DeviceName}' stopped: {FaultMessage}");
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
        try { _render?.Dispose(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        try { _event?.Dispose(); } catch { /* ignore */ }
        try { _device.Dispose(); } catch { /* ignore */ }
    }

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; } catch { return d.ID; }
    }
}
