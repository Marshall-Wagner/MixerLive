using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MixerLive.Core.Audio;
using MixerLive.Core.Model;

namespace MixerLive.App.Dialogs;

/// <summary>
/// "Audio settings…": the one place for the engine settings — sample rate, buffer size (mixer block,
/// in samples), latency preset and resampler — with a status box showing the resulting latency of
/// every open device, and the expert buffer numbers hidden behind a toggle. <see cref="Result"/>
/// holds the chosen settings after OK.
/// </summary>
public partial class AudioSettingsWindow : Window
{
    private sealed record RateChoice(int Hz, string Label);
    private sealed record BlockChoice(double Ms, int Frames, string Label);

    private readonly AudioEngine _engine;
    private bool _ready;

    public EngineSettings? Result { get; private set; }

    public AudioSettingsWindow(AudioEngine engine, EngineSettings current)
    {
        InitializeComponent();
        _engine = engine;

        var rates = EngineSettings.StandardRates.ToList();
        if (!rates.Contains(current.SampleRate)) rates.Add(current.SampleRate);   // e.g. started with --rate
        rates.Sort();
        var rateChoices = rates.Select(hz => new RateChoice(hz, WaveFormats.RateText(hz))).ToList();
        RateBox.ItemsSource = rateChoices;
        RateBox.SelectedItem = rateChoices.First(r => r.Hz == current.SampleRate);

        LatencyBox.ItemsSource = Enum.GetValues<LatencyPreset>();
        LatencyBox.SelectedItem = current.Latency;
        ResamplerBox.ItemsSource = Enum.GetValues<ResampleQuality>();
        ResamplerBox.SelectedItem = current.Resampler;

        var adv = current.Advanced;
        FillBlockChoices(current.SampleRate, adv?.BlockMs ?? AdvancedEngineSettings.DefaultBlockMs);
        InputBox.Text = Text(adv?.InputBufferMs);
        OutputBox.Text = Text(adv?.OutputBufferMs);
        SharedBox.Text = Text(adv?.SharedOutputBufferMs);
        ExclusiveBox.Text = Text(adv?.ExclusiveBufferMs);
        if (adv != null && (adv.InputBufferMs != null || adv.OutputBufferMs != null || adv.SharedOutputBufferMs != null || adv.ExclusiveBufferMs != null))
            ExpertToggle.IsChecked = true;   // something is overridden: show it

        _ready = true;
        UpdateHints();
        UpdateStatus();
    }

    private int SelectedRate => (RateBox.SelectedItem as RateChoice)?.Hz ?? 48000;
    private double SelectedBlockMs => (BlockBox.SelectedItem as BlockChoice)?.Ms ?? AdvancedEngineSettings.DefaultBlockMs;
    private LatencyPreset SelectedLatency => LatencyBox.SelectedItem is LatencyPreset p ? p : LatencyPreset.Normal;

    private static string Text(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>Power-of-two sizes between 1 and 25 ms at this rate, plus the 5 ms default (and the current value if it is neither).</summary>
    private void FillBlockChoices(int rate, double keepMs)
    {
        var list = new List<BlockChoice>();
        int defFrames = EngineOptions.BlockFrames(rate, AdvancedEngineSettings.DefaultBlockMs);
        list.Add(new BlockChoice(AdvancedEngineSettings.DefaultBlockMs, defFrames, $"{defFrames:N0} samples · {AdvancedEngineSettings.DefaultBlockMs:0.#} ms (default)"));
        for (int f = 32; f <= 1 << 16; f *= 2)
        {
            double ms = f * 1000.0 / rate;
            if (ms < AdvancedEngineSettings.MinBlockMs - 1e-9 || ms > 25) continue;
            list.Add(new BlockChoice(ms, f, $"{f:N0} samples · {ms:0.#} ms"));
        }

        var pick = list.FirstOrDefault(c => Math.Abs(c.Ms - keepMs) < 1e-6)
                   ?? list.FirstOrDefault(c => Math.Abs(c.Ms - keepMs) / keepMs < 0.01);
        if (pick == null)
        {
            int frames = EngineOptions.BlockFrames(rate, keepMs);
            pick = new BlockChoice(keepMs, frames, $"{frames:N0} samples · {keepMs:0.#} ms");
            list.Add(pick);
        }
        list.Sort((a, b) => a.Ms.CompareTo(b.Ms));

        bool ready = _ready;
        _ready = false;
        BlockBox.ItemsSource = list;
        BlockBox.SelectedItem = pick;
        _ready = ready;
    }

    private void RateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        FillBlockChoices(SelectedRate, SelectedBlockMs);
        UpdateStatus();
    }

    private void LatencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateHints();
        UpdateStatus();
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateStatus();
    }

    private void ExpertToggled(object sender, RoutedEventArgs e)
    {
        bool on = ExpertToggle.IsChecked == true;
        ExpertPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ExpertToggle.Content = on ? "Hide expert settings" : "Show expert settings";
    }

    /// <summary>Expert hints quote the values of the Latency choice currently selected.</summary>
    private void UpdateHints()
    {
        var p = SelectedLatency;
        InputHint.Text = $"Captured audio kept ahead of the mixer, per input. Latency {p}: {EngineOptions.PresetInputBufferMs(p)} ms " +
                         $"({AdvancedEngineSettings.MinRingMs}–{AdvancedEngineSettings.MaxRingMs}). Shared-mode inputs arrive in 10 ms packets, so go below 12 only with EXCL inputs.";
        OutputHint.Text = $"Mixed audio kept ahead of each output. Latency {p}: {EngineOptions.PresetOutputBufferMs(p)} ms " +
                          $"({AdvancedEngineSettings.MinRingMs}–{AdvancedEngineSettings.MaxRingMs}).";
        SharedHint.Text = $"Buffer asked from Windows for outputs without EXCL. Latency {p}: {EngineOptions.PresetSharedOutputBufferMs(p)} ms " +
                          $"({AdvancedEngineSettings.MinSharedOutputMs}–{AdvancedEngineSettings.MaxSharedOutputMs}).";
        ExclusiveHint.Text = $"Buffer asked from the driver for EXCL inputs and outputs. Latency {p}: at least {EngineOptions.PresetExclusiveBufferMs(p)} ms " +
                             $"({AdvancedEngineSettings.MinExclusiveMs}–{AdvancedEngineSettings.MaxExclusiveMs}); the driver's minimum wins if it is larger.";
    }

    /// <summary>Latency of every open device for the values as chosen, in samples and ms, like a driver's control panel.</summary>
    private void UpdateStatus()
    {
        int rate = SelectedRate;
        double block = SelectedBlockMs;
        var preset = SelectedLatency;
        int input = Value(InputBox) ?? EngineOptions.PresetInputBufferMs(preset);
        int output = Value(OutputBox) ?? EngineOptions.PresetOutputBufferMs(preset);
        int? exclusive = Value(ExclusiveBox);

        var sb = new StringBuilder();
        sb.AppendLine($"Current sample rate: {rate:N0} Hz");
        sb.AppendLine($"Buffer size:         {EngineOptions.BlockFrames(rate, block):N0} samples ({block:0.#} ms)");

        var devices = _engine.OpenDevices();
        if (devices.Count == 0) sb.AppendLine("No device is open right now, so there is no latency to show.");
        else sb.AppendLine();
        foreach (var d in devices)
        {
            // Shared devices: Windows' buffer. Exclusive devices: the override if one is typed, today's driver
            // buffer if nothing that decides it changes, otherwise the new Latency choice's minimum (an estimate).
            bool exclusiveUnchanged = preset == _engine.Options.Latency && _engine.Options.ExclusiveBufferMs == null;
            double period = !d.Exclusive ? d.PeriodMs
                : exclusive is int x ? x
                : exclusiveUnchanged ? d.PeriodMs
                : EngineOptions.PresetExclusiveBufferMs(preset);
            double ms = d.IsInput ? period + input + block : output + period;
            int samples = (int)Math.Round(ms * rate / 1000.0);
            string name = d.Channel.Length > 16 ? d.Channel[..15] + "…" : d.Channel;
            sb.AppendLine($"{(d.IsInput ? "Input " : "Output")} {name,-16} {samples,8:N0} samples ({ms,5:0.0} ms){(d.Exclusive ? "  EXCL" : "")}");
        }
        StatusText.Text = sb.ToString().TrimEnd();
        StatusNote.Text = "Approximate latency MixerLive adds for each device that is open right now, for the values chosen above. " +
                          "Input = the device's buffer + input buffer + buffer size; output = output buffer + the device's buffer. Driver and USB delays come on top.";
    }

    private static int? Value(TextBox box)
        => int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        LatencyBox.SelectedItem = LatencyPreset.Normal;
        ResamplerBox.SelectedItem = ResampleQuality.Best;
        InputBox.Text = OutputBox.Text = SharedBox.Text = ExclusiveBox.Text = "";
        _ready = true;
        FillBlockChoices(SelectedRate, AdvancedEngineSettings.DefaultBlockMs);
        UpdateHints();
        UpdateStatus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Read(InputBox, "Input buffer", AdvancedEngineSettings.MinRingMs, AdvancedEngineSettings.MaxRingMs, out int? input)) return;
        if (!Read(OutputBox, "Output buffer", AdvancedEngineSettings.MinRingMs, AdvancedEngineSettings.MaxRingMs, out int? output)) return;
        if (!Read(SharedBox, "Windows output buffer", AdvancedEngineSettings.MinSharedOutputMs, AdvancedEngineSettings.MaxSharedOutputMs, out int? shared)) return;
        if (!Read(ExclusiveBox, "Device buffer", AdvancedEngineSettings.MinExclusiveMs, AdvancedEngineSettings.MaxExclusiveMs, out int? exclusive)) return;

        double block = SelectedBlockMs;
        Result = new EngineSettings
        {
            SampleRate = SelectedRate,
            Latency = SelectedLatency,
            Resampler = ResamplerBox.SelectedItem is ResampleQuality q ? q : ResampleQuality.Best,
            Advanced = new AdvancedEngineSettings
            {
                BlockMs = Math.Abs(block - AdvancedEngineSettings.DefaultBlockMs) < 1e-9 ? null : block,
                InputBufferMs = input,
                OutputBufferMs = output,
                SharedOutputBufferMs = shared,
                ExclusiveBufferMs = exclusive
            }.Trimmed()
        };
        DialogResult = true;
    }

    /// <summary>Empty = Latency's value (null); otherwise a whole number inside the range, or the user is told why not.</summary>
    private bool Read(TextBox box, string what, int min, int max, out int? value)
    {
        value = null;
        string text = box.Text.Trim();
        if (text.Length == 0) return true;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= min && v <= max)
        {
            value = v;
            return true;
        }
        ExpertToggle.IsChecked = true;
        MessageBox.Show(this, $"{what}: enter a whole number of milliseconds between {min} and {max}, or leave the box empty to keep the Latency setting's value.",
            "Audio settings", MessageBoxButton.OK, MessageBoxImage.Information);
        box.Focus();
        box.SelectAll();
        return false;
    }
}
