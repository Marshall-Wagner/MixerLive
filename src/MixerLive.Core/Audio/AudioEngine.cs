using System.Diagnostics;
using System.Text;
using MixerLive.Core.Logging;
using MixerLive.Core.Model;
using NAudio.CoreAudioApi;

namespace MixerLive.Core.Audio;

public sealed class EngineOptions
{
    public int SampleRate { get; set; } = 48000;
    public LatencyPreset Latency { get; set; } = LatencyPreset.Normal;
    public ResampleQuality Resampler { get; set; } = ResampleQuality.Best;

    /// <summary>Buffer size and expert overrides from the Audio settings window; null or empty = the defaults.</summary>
    public AdvancedEngineSettings? Advanced { get; set; }

    public static EngineOptions From(EngineSettings s)
    {
        var o = new EngineOptions();
        s.ApplyTo(o);
        return o;
    }

    /// <summary>Mixer block length in ms (5 unless overridden).</summary>
    public double BlockMs => Math.Clamp(Advanced?.BlockMs ?? AdvancedEngineSettings.DefaultBlockMs,
        AdvancedEngineSettings.MinBlockMs, AdvancedEngineSettings.MaxBlockMs);

    /// <summary>Frames per mixer block at the given rate.</summary>
    public int BlockFramesFor(int rate) => BlockFrames(rate, BlockMs);

    /// <summary>Frames per mixer block for a rate and block length (never below 32).</summary>
    public static int BlockFrames(int rate, double blockMs) => Math.Max(32, (int)Math.Round(rate * blockMs / 1000.0));

    /// <summary>
    /// Target fill of an input strip's ring buffer. The capture side delivers ~10 ms packets in
    /// shared mode and the mixer takes blocks, so the fill must cover one packet plus jitter.
    /// </summary>
    public int StripRingTargetMs => Math.Clamp(Advanced?.InputBufferMs ?? PresetInputBufferMs(Latency),
        AdvancedEngineSettings.MinRingMs, AdvancedEngineSettings.MaxRingMs);

    /// <summary>
    /// Target fill of a bus ring buffer (measured just before the render thread pulls a chunk),
    /// so it must cover one chunk plus one mixer block plus jitter.
    /// </summary>
    public int BusRingTargetMs => Math.Clamp(Advanced?.OutputBufferMs ?? PresetOutputBufferMs(Latency),
        AdvancedEngineSettings.MinRingMs, AdvancedEngineSettings.MaxRingMs);

    /// <summary>Requested WASAPI render buffer length in shared mode.</summary>
    public int RenderBufferMs => Math.Clamp(Advanced?.SharedOutputBufferMs ?? PresetSharedOutputBufferMs(Latency),
        AdvancedEngineSettings.MinSharedOutputMs, AdvancedEngineSettings.MaxSharedOutputMs);

    /// <summary>
    /// Advanced override of the exclusive-mode driver buffer; null = the preset rule (Low: the
    /// driver's minimum, at least 3 ms; Normal: its default, at least 10 ms; Safe: twice that).
    /// </summary>
    public int? ExclusiveBufferMs => Advanced?.ExclusiveBufferMs is int ms
        ? Math.Clamp(ms, AdvancedEngineSettings.MinExclusiveMs, AdvancedEngineSettings.MaxExclusiveMs)
        : null;

    public static int PresetInputBufferMs(LatencyPreset p) => p switch { LatencyPreset.Low => 12, LatencyPreset.Safe => 40, _ => 20 };
    public static int PresetOutputBufferMs(LatencyPreset p) => p switch { LatencyPreset.Low => 20, LatencyPreset.Safe => 50, _ => 25 };
    public static int PresetSharedOutputBufferMs(LatencyPreset p) => p switch { LatencyPreset.Low => 15, LatencyPreset.Safe => 40, _ => 20 };

    /// <summary>Smallest exclusive-mode buffer the preset asks for; the driver may insist on more.</summary>
    public static int PresetExclusiveBufferMs(LatencyPreset p) => p switch { LatencyPreset.Low => 3, LatencyPreset.Safe => 20, _ => 10 };

    /// <summary>"768000 Hz, 5 ms blocks (3840 frames), latency Normal, resampler Best" plus any overrides.</summary>
    public string Describe()
    {
        string s = $"{SampleRate} Hz, {BlockMs:0.#} ms blocks ({BlockFramesFor(SampleRate)} frames), latency preset {Latency}, resampler {Resampler}";
        if (Advanced is { IsEmpty: false } a) s += $", advanced: {a.Describe()}";
        return s;
    }
}

/// <summary>Live status of one strip or bus, read by the UI.</summary>
public sealed class ChannelStatus
{
    public string Text = "No device selected";
    public bool IsBound;
    public bool IsWaiting;
    public bool IsError;
    public float PeakL;
    public float PeakR;
    public int Underruns;
    public int Overruns;
    public long Frames;
    public double CorrectionPpm;
}

/// <summary>
/// The mixer. A timer-paced thread pulls a 5 ms block from every input strip, applies the
/// strip processing, sums routed strips into the five buses, applies bus processing and
/// pushes the result to the render streams. Capture/render streams run on their own threads
/// and are decoupled through drift-correcting ring buffers, so any device can appear,
/// disappear or drift without stalling the others.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private sealed class StripRuntime
    {
        public required StripSettings Settings;
        public required StripProcessor Dsp;
        public required float[] Block;
        public CaptureStream? Stream;
        public AdaptiveReader? Reader;
        public ChannelStatus Status = new();
        public DateTime LastAttempt;
        public string? LastError;
    }

    private sealed class BusRuntime
    {
        public required BusSettings Settings;
        public required BusProcessor Dsp;
        public required float[] Mix;
        public RenderStream? Stream;
        public ChannelStatus Status = new();
        public DateTime LastAttempt;
        public string? LastError;
    }

    private readonly StripRuntime[] _strips = new StripRuntime[MixerProfile.StripCount];
    private readonly BusRuntime[] _buses = new BusRuntime[MixerProfile.BusCount];
    private readonly object _bindLock = new();
    private int _ringFrames;
    private Thread? _mixThread;
    private Timer? _watchdog;
    private volatile bool _stop;
    private volatile bool _running;
    private int _rebindQueued;
    private int _forceRebind;
    private double _cpu;

    /// <summary>How long calls from the UI wait for a device reconnect in progress before giving up.</summary>
    private static readonly TimeSpan BindLockTimeout = TimeSpan.FromSeconds(8);
    /// <summary>A reconnect that takes longer than this is reported in the log as stuck.</summary>
    private static readonly TimeSpan RebindStuckAfter = TimeSpan.FromSeconds(15);
    /// <summary>Stopwatch timestamp at which the running reconnect started; 0 while none runs.</summary>
    private long _rebindSince;
    private long _rebindWarnedFor;

    public MixerProfile Profile { get; private set; }
    public EngineOptions Options { get; }
    public DeviceService Devices { get; }
    public int BlockFrames { get; private set; }
    public bool IsRunning => _running;
    public double CpuLoad => _cpu;

    /// <summary>Raised (on an arbitrary thread) when binding state changes.</summary>
    public event Action? StatusChanged;

    public AudioEngine(MixerProfile profile, EngineOptions options, DeviceService devices)
    {
        Profile = profile;
        Options = options;
        Devices = devices;
        BuildRuntimes();
    }

    /// <summary>
    /// Takes <see cref="_bindLock"/> for a call that comes from the UI. A reconnect holds the lock
    /// while it talks to Windows; if a Windows call never returns, waiting forever would freeze the
    /// window, so give up after a few seconds with an explanation instead.
    /// </summary>
    private void EnterBindLock(string action)
    {
        if (Monitor.TryEnter(_bindLock, BindLockTimeout)) return;
        long since = Volatile.Read(ref _rebindSince);
        string detail = since != 0 ? $"; reconnecting devices has been busy for {Stopwatch.GetElapsedTime(since).TotalSeconds:0} s" : "";
        Log.Error($"Could not {action}: the audio engine did not respond within {BindLockTimeout.TotalSeconds:0} s{detail}");
        throw new TimeoutException($"The audio engine is not responding, so it could not {action}.\n\n" +
                                   "Exit MixerLive from the tray icon and start it again. Details are in the log (Logs button).");
    }

    /// <summary>(Re)creates the per-channel DSP and buffers for the current sample rate. Engine must be stopped.</summary>
    private void BuildRuntimes()
    {
        EnterBindLock("apply the audio settings");
        try
        {
            int rate = Options.SampleRate;
            BlockFrames = Options.BlockFramesFor(rate);
            _ringFrames = RingFramesFor(rate);

            for (int i = 0; i < MixerProfile.StripCount; i++)
            {
                _strips[i] = new StripRuntime
                {
                    Settings = Profile.Strips[i],
                    Dsp = new StripProcessor(rate),
                    Block = new float[BlockFrames * 2]
                };
            }
            for (int i = 0; i < MixerProfile.BusCount; i++)
            {
                _buses[i] = new BusRuntime
                {
                    Settings = Profile.Buses[i],
                    Dsp = new BusProcessor(rate),
                    Mix = new float[BlockFrames * 2]
                };
            }
        }
        finally
        {
            Monitor.Exit(_bindLock);
        }
    }

    /// <summary>Ring capacity: at least 1.3 s at 48 kHz, scaling so that higher rates keep ≥ 340 ms.</summary>
    private static int RingFramesFor(int rate)
    {
        int min = Math.Max(1 << 16, rate / 4);
        int cap = 1 << 16;
        while (cap < min) cap <<= 1;
        return cap;
    }

    public ChannelStatus StripStatus(int index) => _strips[index].Status;
    public ChannelStatus BusStatus(int index) => _buses[index].Status;

    /// <summary>A device that is currently open, with the buffer its driver or Windows delivers in.</summary>
    public sealed record OpenDevice(string Channel, string Device, bool IsInput, bool Exclusive, double PeriodMs);

    /// <summary>The devices currently open, for latency displays.</summary>
    public IReadOnlyList<OpenDevice> OpenDevices()
    {
        var list = new List<OpenDevice>();
        if (!Monitor.TryEnter(_bindLock, BindLockTimeout)) return list; // engine busy: show nothing rather than freeze
        try
        {
            foreach (var rt in _strips)
            {
                if (rt.Stream is { Faulted: false } s)
                    list.Add(new OpenDevice(string.IsNullOrWhiteSpace(rt.Settings.Label) ? $"Input {rt.Settings.Index + 1}" : rt.Settings.Label,
                        s.DeviceName, true, s.Exclusive, s.PeriodMs));
            }
            foreach (var rt in _buses)
            {
                if (rt.Stream is { Faulted: false } s)
                    list.Add(new OpenDevice(MixerProfile.BusName(rt.Settings.Index) + (string.IsNullOrWhiteSpace(rt.Settings.Label) ? "" : $" {rt.Settings.Label}"),
                        s.DeviceName, false, s.Exclusive, s.PeriodMs));
            }
        }
        finally
        {
            Monitor.Exit(_bindLock);
        }
        return list;
    }

    public void Start()
    {
        EnterBindLock("start the audio engine");
        try
        {
            if (_running) return;
            _stop = false;
            _running = true;
            _mixThread = new Thread(MixLoop)
            {
                IsBackground = true,
                Name = "MixerLive mixer",
                Priority = ThreadPriority.Highest
            };
            _mixThread.Start();
            Devices.DevicesChanged += OnDevicesChanged;
            _watchdog = new Timer(_ => Watchdog(), null, 3000, 3000);
        }
        finally
        {
            Monitor.Exit(_bindLock);
        }
        Log.Info($"Engine started: {Options.Describe()}");
        RequestRebind(force: true);
        RaiseStatusChanged();
    }

    public void Stop()
    {
        Thread? t;
        EnterBindLock("stop the audio engine");
        try
        {
            if (!_running) return;
            _running = false;
            _stop = true;
            t = _mixThread;
            _mixThread = null;
            Devices.DevicesChanged -= OnDevicesChanged;
            _watchdog?.Dispose();
            _watchdog = null;
        }
        finally
        {
            Monitor.Exit(_bindLock);
        }
        t?.Join(2000);
        var sw = Stopwatch.StartNew();
        Apartment.RunOnMta(() =>
        {
            EnterBindLock("close the audio devices");
            try
            {
                foreach (var rt in _strips)
                {
                    long t0 = sw.ElapsedMilliseconds;
                    DropStream(rt);
                    long ms = sw.ElapsedMilliseconds - t0;
                    if (ms > 250) Log.Warn($"Closing strip {rt.Settings.Index + 1} took {ms} ms");
                    SetStatus(rt.Status, "Engine stopped", bound: false, waiting: false, error: false);
                }
                foreach (var rt in _buses)
                {
                    long t0 = sw.ElapsedMilliseconds;
                    DropStream(rt);
                    long ms = sw.ElapsedMilliseconds - t0;
                    if (ms > 250) Log.Warn($"Closing bus {MixerProfile.BusName(rt.Settings.Index)} took {ms} ms");
                    SetStatus(rt.Status, "Engine stopped", bound: false, waiting: false, error: false);
                }
            }
            finally
            {
                Monitor.Exit(_bindLock);
            }
        });
        Log.Info($"Engine stopped ({sw.ElapsedMilliseconds} ms to close streams)");
        RaiseStatusChanged();
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>
    /// Applies whatever changed in <see cref="Options"/> (rate, block length, buffers, latency,
    /// resampler): rebuilds the DSP and buffers and restarts the engine if it was running.
    /// </summary>
    public void ApplyOptions()
    {
        bool wasRunning = IsRunning;
        Stop();
        BuildRuntimes();
        Log.Info($"Engine options applied: {Options.Describe()}");
        if (wasRunning) Start();
    }

    /// <summary>Swaps in a new profile (e.g. after an import) and rebinds every device.</summary>
    public void ReplaceProfile(MixerProfile profile)
    {
        profile.Normalize();
        EnterBindLock("load the profile");
        try
        {
            Profile = profile;
            for (int i = 0; i < MixerProfile.StripCount; i++) _strips[i].Settings = profile.Strips[i];
            for (int i = 0; i < MixerProfile.BusCount; i++) _buses[i].Settings = profile.Buses[i];
        }
        finally
        {
            Monitor.Exit(_bindLock);
        }
        RequestRebind(force: true);
    }

    /// <summary>Asks the engine to (re)open devices on a background thread; calls are coalesced.</summary>
    public void RequestRebind(bool force = false)
    {
        if (force) Volatile.Write(ref _forceRebind, 1);
        if (Interlocked.Exchange(ref _rebindQueued, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(50);
                Interlocked.Exchange(ref _rebindQueued, 0);
                bool f = Interlocked.Exchange(ref _forceRebind, 0) == 1;
                Rebind(f);
            }
            catch (Exception ex)
            {
                Log.Error("Rebind failed", ex);
            }
        });
    }

    private void OnDevicesChanged()
    {
        Log.Info("Audio device change detected");
        RequestRebind();
    }

    /// <summary>How often devices that are simply not plugged in / installed are looked for again.
    /// Normally they bind through device-change notifications long before this.</summary>
    private static readonly TimeSpan MissingDeviceProbeInterval = TimeSpan.FromMinutes(2);
    private DateTime _lastMissingProbe = DateTime.UtcNow;

    /// <summary>
    /// Every 3 s: retries streams that broke or devices that exist but could not be opened (e.g. held
    /// in exclusive mode by another program). Devices that are absent are NOT polled every 3 s:
    /// looking them up means a full endpoint scan, and Windows already notifies us when they appear.
    /// </summary>
    private void Watchdog()
    {
        if (!_running) return;

        // A reconnect in progress: don't queue more behind it, but say so in the log if it hangs.
        long since = Volatile.Read(ref _rebindSince);
        if (since != 0)
        {
            var busy = Stopwatch.GetElapsedTime(since);
            if (busy > RebindStuckAfter && Interlocked.Exchange(ref _rebindWarnedFor, since) != since)
                Log.Warn($"Reconnecting devices has not finished after {busy.TotalSeconds:0} s: a Windows audio call is not returning");
            return;
        }

        bool retry = false, missing = false;

        void Check(DeviceRef device, bool hasStream, bool faulted, ChannelStatus status)
        {
            if (device.IsEmpty) return;
            if (hasStream && faulted) retry = true;            // stream died
            else if (!hasStream && status.IsError) retry = true; // present but could not be opened
            else if (!hasStream) missing = true;                 // not connected / not installed
        }

        foreach (var rt in _strips)
        {
            var s = rt.Stream;
            Check(rt.Settings.Device, s != null, s?.Faulted == true, rt.Status);
        }
        foreach (var rt in _buses)
        {
            var s = rt.Stream;
            Check(rt.Settings.Device, s != null, s?.Faulted == true, rt.Status);
        }

        if (!retry && missing && DateTime.UtcNow - _lastMissingProbe >= MissingDeviceProbeInterval)
            retry = true;
        if (retry)
        {
            _lastMissingProbe = DateTime.UtcNow;
            RequestRebind();
        }
    }

    private void Rebind(bool force)
    {
        lock (_bindLock)
        {
            if (!_running) return;
            Volatile.Write(ref _rebindSince, Stopwatch.GetTimestamp());
            try
            {
                foreach (var rt in _strips) BindStrip(rt, force);
                foreach (var rt in _buses) BindBus(rt, force);
            }
            finally
            {
                long started = Interlocked.Exchange(ref _rebindSince, 0);
                if (Interlocked.Read(ref _rebindWarnedFor) == started)
                    Log.Info($"Reconnecting devices finished after {Stopwatch.GetElapsedTime(started).TotalSeconds:0} s");
            }
        }
        RaiseStatusChanged();
    }

    private void BindStrip(StripRuntime rt, bool force)
    {
        var s = rt.Settings;
        var want = s.Device;
        if (want.IsEmpty)
        {
            DropStream(rt);
            SetStatus(rt.Status, "No device selected", bound: false, waiting: false, error: false);
            return;
        }

        bool wantExclusive = s.Exclusive && !s.IsVirtual;
        var flow = s.IsVirtual ? DataFlow.Render : DataFlow.Capture;
        MMDevice? dev = null;
        try { dev = Devices.Resolve(want, flow); }
        catch (Exception ex) { Log.Error("Device lookup failed", ex); }

        if (dev == null)
        {
            DropStream(rt);
            SetStatus(rt.Status, $"Waiting for device: {want.Name}", bound: false, waiting: true, error: false);
            return;
        }

        var cur = rt.Stream;
        if (cur != null && !cur.Faulted && cur.DeviceId == dev.ID && cur.Exclusive == wantExclusive && !force)
        {
            dev.Dispose();
            return;
        }
        if (cur != null && cur.Faulted && !force && DateTime.UtcNow - rt.LastAttempt < TimeSpan.FromSeconds(5))
        {
            dev.Dispose();
            return;
        }

        DropStream(rt);
        rt.LastAttempt = DateTime.UtcNow;
        string name = SafeName(dev);
        var stream = new CaptureStream(dev, s.IsVirtual, Options.SampleRate, _ringFrames, wantExclusive, Options.Latency, Options.ExclusiveBufferMs);
        try
        {
            stream.Open();
        }
        catch (Exception ex)
        {
            stream.Dispose();
            string msg = $"Cannot open \"{name}\"{(wantExclusive ? " in exclusive mode" : "")}: {AudioErrors.Describe(ex)}";
            if (rt.LastError != msg)
            {
                Log.Warn($"Strip {s.Index + 1}: {msg}");
                rt.LastError = msg;
            }
            SetStatus(rt.Status, msg, bound: false, waiting: false, error: true);
            return;
        }

        var reader = new AdaptiveReader(stream.Ring)
        {
            NominalRatio = stream.SourceRate / (double)Options.SampleRate,
            SourceRate = stream.SourceRate,
            TargetFill = Math.Max(BlockFrames * 2, (int)(Options.StripRingTargetMs * stream.SourceRate / 1000.0)),
            Quality = Options.Resampler
        };
        reader.Reset();
        stream.Failed += _ => RequestRebind();

        rt.Reader = reader;
        Volatile.Write(ref rt.Stream, stream);
        stream.Start();
        rt.LastError = null;

        string resampler = stream.SourceRate != Options.SampleRate ? $" · {reader.Description}" : "";
        SetStatus(rt.Status, $"{name} · {stream.ModeDescription}{resampler}", bound: true, waiting: false, error: false);
        Log.Info($"Strip {s.Index + 1} → \"{name}\" ({stream.ModeDescription}{resampler}, {stream.SourceChannels} ch)");
    }

    private void BindBus(BusRuntime rt, bool force)
    {
        var b = rt.Settings;
        var want = b.Device;
        if (want.IsEmpty)
        {
            DropStream(rt);
            SetStatus(rt.Status, "No device selected", bound: false, waiting: false, error: false);
            return;
        }

        MMDevice? dev = null;
        try { dev = Devices.Resolve(want, DataFlow.Render); }
        catch (Exception ex) { Log.Error("Device lookup failed", ex); }

        if (dev == null)
        {
            DropStream(rt);
            SetStatus(rt.Status, $"Waiting for device: {want.Name}", bound: false, waiting: true, error: false);
            return;
        }

        var cur = rt.Stream;
        if (cur != null && !cur.Faulted && cur.DeviceId == dev.ID && cur.Exclusive == b.Exclusive && !force)
        {
            dev.Dispose();
            return;
        }
        if (cur != null && cur.Faulted && !force && DateTime.UtcNow - rt.LastAttempt < TimeSpan.FromSeconds(5))
        {
            dev.Dispose();
            return;
        }

        DropStream(rt);
        rt.LastAttempt = DateTime.UtcNow;
        string name = SafeName(dev);
        var stream = new RenderStream(dev, Options.SampleRate, _ringFrames, b.Exclusive, Options.Latency, Options.RenderBufferMs, Options.BusRingTargetMs, Options.ExclusiveBufferMs);
        stream.Reader.Quality = Options.Resampler;
        try
        {
            stream.Open();
        }
        catch (Exception ex)
        {
            stream.Dispose();
            string msg = $"Cannot open \"{name}\"{(b.Exclusive ? " in exclusive mode" : "")}: {AudioErrors.Describe(ex)}";
            if (rt.LastError != msg)
            {
                Log.Warn($"Bus {MixerProfile.BusName(b.Index)}: {msg}");
                rt.LastError = msg;
            }
            SetStatus(rt.Status, msg, bound: false, waiting: false, error: true);
            return;
        }

        stream.Failed += _ => RequestRebind();
        Volatile.Write(ref rt.Stream, stream);
        stream.Start();
        rt.LastError = null;

        string resampler = stream.DeviceRate != Options.SampleRate ? $" · {stream.Reader.Description}" : "";
        SetStatus(rt.Status, $"{name} · {stream.ModeDescription}{resampler}", bound: true, waiting: false, error: false);
        Log.Info($"Bus {MixerProfile.BusName(b.Index)} → \"{name}\" ({stream.ModeDescription}{resampler}, {stream.DeviceChannels} ch, buffer {stream.BufferFrames} frames)");
    }

    private static void DropStream(StripRuntime rt)
    {
        var s = rt.Stream;
        Volatile.Write(ref rt.Stream, null);
        rt.Reader = null;
        s?.Dispose();
    }

    private static void DropStream(BusRuntime rt)
    {
        var s = rt.Stream;
        Volatile.Write(ref rt.Stream, null);
        s?.Dispose();
    }

    private static void SetStatus(ChannelStatus st, string text, bool bound, bool waiting, bool error)
    {
        st.Text = text;
        st.IsBound = bound;
        st.IsWaiting = waiting;
        st.IsError = error;
        if (!bound)
        {
            st.Frames = 0;
            st.Underruns = 0;
            st.Overruns = 0;
            st.CorrectionPpm = 0;
        }
    }

    private void RaiseStatusChanged()
    {
        try { StatusChanged?.Invoke(); } catch (Exception ex) { Log.Error("StatusChanged handler failed", ex); }
    }

    private void MixLoop()
    {
        // The mixer runs at MMCSS "Pro Audio" priority, so it must never busy-wait: a spinning
        // real-time thread competes with games and with Windows' own audio engine.
        using var pacer = new PrecisionPacer();
        IntPtr mm = Native.EnterProAudio();
        int rate = Options.SampleRate;
        int n = BlockFrames;
        long early = Math.Max(1, rate / 10_000);   // may run a block up to 0.1 ms early instead of waiting again
        double ticksPerFrame = Stopwatch.Frequency / (double)rate;
        var sw = Stopwatch.StartNew();
        long produced = 0;
        Log.Info(pacer.HighResolution
            ? "Mixer pacing: high-resolution waitable timer"
            : "Mixer pacing: Sleep with 1 ms timer resolution (high-resolution timer unavailable)");

        try
        {
            while (!_stop)
            {
                long due = (long)(sw.ElapsedTicks / ticksPerFrame) - produced;
                if (due > rate / 2)
                {
                    // We were suspended (sleep, debugger, heavy stall): skip ahead rather than burst.
                    produced += due - n;
                    due = n;
                }

                if (due >= n - early)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    ProcessBlock();
                    long t1 = Stopwatch.GetTimestamp();
                    produced += n;
                    double load = (t1 - t0) / (ticksPerFrame * n);
                    _cpu += 0.02 * (load - _cpu);
                    continue;
                }

                pacer.Wait((n - due) * 1000.0 / rate);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mixer thread crashed", ex);
        }
        finally
        {
            Native.LeaveProAudio(mm);
        }
    }

    private void ProcessBlock()
    {
        int n = BlockFrames;
        var strips = _strips;
        var buses = _buses;

        bool anySolo = false;
        foreach (var rt in strips)
            if (rt.Settings.Solo) { anySolo = true; break; }

        foreach (var rt in strips)
        {
            var st = rt.Stream;
            var rd = rt.Reader;
            var block = rt.Block.AsSpan(0, n * 2);
            bool got = st != null && rd != null && !st.Faulted && rd.Read(block, n);
            if (!got) block.Clear();

            rt.Dsp.Process(block, n, rt.Settings, anySolo);

            var status = rt.Status;
            if (rt.Dsp.PeakL > status.PeakL) status.PeakL = rt.Dsp.PeakL;
            if (rt.Dsp.PeakR > status.PeakR) status.PeakR = rt.Dsp.PeakR;
            if (rd != null)
            {
                status.Underruns = rd.Underruns;
                status.Overruns = rd.Overruns;
                status.CorrectionPpm = rd.CorrectionPpm;
            }
            if (st != null) status.Frames = st.FramesCaptured;
        }

        for (int b = 0; b < buses.Length; b++)
        {
            var brt = buses[b];
            var st = brt.Stream;
            // A bus without an open device (none selected, not connected, or failed) has nowhere to
            // send audio, so it is neither mixed nor metered: its meter reads silence.
            if (st == null || st.Faulted) continue;

            var mix = brt.Mix.AsSpan(0, n * 2);
            mix.Clear();

            foreach (var rt in strips)
            {
                if (!rt.Settings.RoutedTo(b)) continue;
                var src = rt.Block.AsSpan(0, n * 2);
                for (int i = 0; i < src.Length; i++) mix[i] += src[i];
            }

            brt.Dsp.Process(mix, n, brt.Settings);

            var status = brt.Status;
            if (brt.Dsp.PeakL > status.PeakL) status.PeakL = brt.Dsp.PeakL;
            if (brt.Dsp.PeakR > status.PeakR) status.PeakR = brt.Dsp.PeakR;

            st.Ring.Write(mix, n);
            status.Underruns = st.Reader.Underruns;
            status.Overruns = st.Reader.Overruns;
            status.CorrectionPpm = st.Reader.CorrectionPpm;
            status.Frames = st.FramesRendered;
        }
    }

    /// <summary>Human-readable snapshot used by --selftest and the log.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Engine: {(IsRunning ? "running" : "stopped")}, {Options.Describe()}, load {CpuLoad * 100:F1}% of one core");
        for (int i = 0; i < _strips.Length; i++)
        {
            var rt = _strips[i];
            var st = rt.Status;
            sb.AppendLine($"Strip {i + 1} [{rt.Settings.Label}] wanted='{rt.Settings.Device}'{(rt.Settings.Exclusive && !rt.Settings.IsVirtual ? " (exclusive)" : "")} → {st.Text} | frames={st.Frames} under={st.Underruns} over={st.Overruns} corr={st.CorrectionPpm:F0}ppm peak={Dsp.DspUtil.LinToDb(Math.Max(st.PeakL, st.PeakR)):F1}dB");
        }
        for (int i = 0; i < _buses.Length; i++)
        {
            var rt = _buses[i];
            var st = rt.Status;
            sb.AppendLine($"Bus {MixerProfile.BusName(i)} [{rt.Settings.Label}] wanted='{rt.Settings.Device}'{(rt.Settings.Exclusive ? " (exclusive)" : "")} → {st.Text} | frames={st.Frames} under={st.Underruns} over={st.Overruns} corr={st.CorrectionPpm:F0}ppm peak={Dsp.DspUtil.LinToDb(Math.Max(st.PeakL, st.PeakR)):F1}dB");
        }
        return sb.ToString();
    }

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; } catch { return d.ID; }
    }

    public void Dispose()
    {
        Stop();
    }
}
