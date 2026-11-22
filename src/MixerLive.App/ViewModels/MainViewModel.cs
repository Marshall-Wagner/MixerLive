using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MixerLive.App.Dialogs;
using MixerLive.Core.Audio;
using MixerLive.Core.Compat;
using MixerLive.Core.Import;
using MixerLive.Core.Logging;
using MixerLive.Core.Model;
using MixerLive.Core.Startup;
using NAudio.CoreAudioApi;

namespace MixerLive.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _saveTimer;
    private int _tick;
    private bool _voicemeeterRunning;
    private bool _checkingVoicemeeter;
    private bool _runAtLogon;
    private string _engineStatusText = "";
    private string _engineStatusDetail = "";
    private bool _disposed;

    public AudioEngine Engine { get; }
    public AppSettings Settings { get; }

    public ObservableCollection<StripViewModel> Strips { get; } = new();
    public ObservableCollection<BusViewModel> Buses { get; } = new();

    public ICommand ToggleEngineCommand { get; }
    public ICommand RestartEngineCommand { get; }
    public ICommand ImportVoicemeeterCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand SaveProfileAsCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenAudioSettingsCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand CloseVoicemeeterCommand { get; }
    public ICommand ExitCommand { get; }

    public event Action? ExitRequested;
    public event Action? ShowRequested;

    public MainViewModel(AudioEngine engine, AppSettings settings)
    {
        Engine = engine;
        Settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        ToggleEngineCommand = new RelayCommand(ToggleEngine);
        RestartEngineCommand = new RelayCommand(() => RunGuarded(Engine.Restart));
        ImportVoicemeeterCommand = new RelayCommand(ImportVoicemeeterInteractive);
        LoadProfileCommand = new RelayCommand(LoadProfileInteractive);
        SaveProfileAsCommand = new RelayCommand(SaveProfileAsInteractive);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        OpenAudioSettingsCommand = new RelayCommand(OpenAudioSettings);
        AboutCommand = new RelayCommand(OpenAbout);
        CloseVoicemeeterCommand = new RelayCommand(CloseVoicemeeter);
        ExitCommand = new RelayCommand(RequestExit);

        BuildChannels();

        Engine.StatusChanged += () => _dispatcher.BeginInvoke(new Action(OnEngineStatusChanged));
        Engine.Devices.DevicesChanged += () => _dispatcher.BeginInvoke(new Action(RefreshDevicesAsync));

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        RefreshDevicesAsync();
        QueryRunAtLogonAsync();
        UpdateEngineStatusText();
    }

    // ---- bindable state ----

    public bool EngineRunning => Engine.IsRunning;
    public string EngineButtonText => Engine.IsRunning ? "Stop engine" : "Start engine";
    public string ProfileName => Engine.Profile.Name;

    /// <summary>Short status for the status bar and tray tooltip.</summary>
    public string EngineStatusText
    {
        get => _engineStatusText;
        private set => Set(ref _engineStatusText, value);
    }

    /// <summary>Long status (block frames, buffer overrides) shown as the status bar's tooltip.</summary>
    public string EngineStatusDetail
    {
        get => _engineStatusDetail;
        private set => Set(ref _engineStatusDetail, value);
    }

    public bool VoicemeeterRunning
    {
        get => _voicemeeterRunning;
        private set => Set(ref _voicemeeterRunning, value);
    }

    public bool RunAtLogon
    {
        get => _runAtLogon;
        set
        {
            if (_runAtLogon == value) return;
            try
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path");
                if (value) StartupTask.Install(exe);
                else StartupTask.Uninstall();
                _runAtLogon = value;
            }
            catch (Exception ex)
            {
                Log.Error("Changing the logon task failed", ex);
                MessageBox.Show($"Could not change the sign-in task:\n{ex.Message}", "MixerLive", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            OnPropertyChanged();
        }
    }


    /// <summary>
    /// Lock like Voicemeeter's "Lock GUI": faders, buttons, devices and settings ignore the mouse,
    /// wheel and keyboard until unlocked. Meters keep running. Remembered across restarts.
    /// </summary>
    public bool IsLocked
    {
        get => Settings.Locked;
        set
        {
            if (Settings.Locked == value) return;
            Settings.Locked = value;
            ProfileStore.SaveSettings(Settings);
            Log.Info(value ? "Mixer locked" : "Mixer unlocked");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUnlocked));
            OnPropertyChanged(nameof(LockText));
            OnPropertyChanged(nameof(LockGlyph));
        }
    }

    public bool IsUnlocked => !Settings.Locked;
    public string LockText => Settings.Locked ? "Locked" : "Lock";

    /// <summary>Segoe Fluent Icons / MDL2: E72E = closed lock, E785 = open lock.</summary>
    public string LockGlyph => Settings.Locked ? "" : "";

    public bool AutoStartEngine
    {
        get => Settings.AutoStartEngine;
        set
        {
            if (Settings.AutoStartEngine == value) return;
            Settings.AutoStartEngine = value;
            ProfileStore.SaveSettings(Settings);
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => Settings.CloseToTray;
        set
        {
            if (Settings.CloseToTray == value) return;
            Settings.CloseToTray = value;
            Settings.MinimizeToTray = value;
            ProfileStore.SaveSettings(Settings);
            OnPropertyChanged();
        }
    }

    // ---- actions ----

    public void RequestExit() => ExitRequested?.Invoke();
    public void ShowWindow() => ShowRequested?.Invoke();

    private void ToggleEngine()
    {
        if (IsLocked) return;
        RunGuarded(() =>
        {
            if (Engine.IsRunning) Engine.Stop();
            else Engine.Start();
        });
        OnEngineStatusChanged();
    }

    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Engine operation failed", ex);
            MessageBox.Show(ex.Message, "MixerLive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void MarkDirty()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        ProfileStore.SaveProfile(Engine.Profile);
    }

    public void OpenBusEq(BusViewModel bus) => OpenEq(bus.Eq);

    /// <summary>Opens the parametric EQ editor of a strip or a bus.</summary>
    public void OpenEq(EqEditorViewModel eq)
    {
        var wnd = new BusEqWindow { DataContext = eq, Owner = Application.Current.MainWindow };
        wnd.ShowDialog();
    }

    /// <summary>First launch: offer to import the auto-saved Voicemeeter Banana settings.</summary>
    public void OfferFirstRunImport()
    {
        Settings.FirstRunDone = true;
        ProfileStore.SaveSettings(Settings);

        // Already running on an imported profile (e.g. via --import): nothing to offer.
        if (Engine.Profile.SourceFile != null) return;

        string path = VoicemeeterXmlImporter.LastSettingsPath;
        if (!File.Exists(path)) return;

        var answer = MessageBox.Show(
            $"Voicemeeter Banana settings were found:\n{path}\n\nImport them now? (Devices, labels, routing, faders, mute/solo, gate/comp and bus EQ are copied.)",
            "MixerLive – first start", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) ImportFromFile(path);
    }

    private void ImportVoicemeeterInteractive()
    {
        if (IsLocked) return;
        string dir = Directory.Exists(VoicemeeterXmlImporter.DefaultSettingsDirectory)
            ? VoicemeeterXmlImporter.DefaultSettingsDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Voicemeeter Banana settings",
            Filter = "Voicemeeter settings (*.xml)|*.xml|All files (*.*)|*.*",
            InitialDirectory = dir,
            FileName = File.Exists(VoicemeeterXmlImporter.LastSettingsPath) ? Path.GetFileName(VoicemeeterXmlImporter.LastSettingsPath) : ""
        };
        if (dlg.ShowDialog() != true) return;
        ImportFromFile(dlg.FileName);
    }

    public void ImportFromFile(string path)
    {
        VoicemeeterXmlImporter.ImportResult result;
        try
        {
            result = VoicemeeterXmlImporter.ImportFile(path);
        }
        catch (Exception ex)
        {
            Log.Error($"Import of '{path}' failed", ex);
            MessageBox.Show($"Could not read the file:\n{ex.Message}", "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var summary = BuildImportSummary(result);
        var preview = new ImportPreviewWindow(path, summary) { Owner = Application.Current.MainWindow };
        if (preview.ShowDialog() != true) return;

        ApplyProfile(result.Profile);
        Log.Info($"Imported Voicemeeter settings from {path}");
    }

    private string BuildImportSummary(VoicemeeterXmlImporter.ImportResult r)
    {
        var p = r.Profile;
        var capture = Engine.Devices.GetDevices(DataFlow.Capture).Select(d => (d.Id, d.Name)).ToList();
        var render = Engine.Devices.GetDevices(DataFlow.Render).Select(d => (d.Id, d.Name)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("INPUT STRIPS");
        foreach (var s in p.Strips)
        {
            sb.Append($"  {MixerProfile.StripTitle(s.Index),-18} {Quote(s.Label),-16} ");
            sb.Append(DescribeDevice(s.Device, s.IsVirtual ? render : capture, s.IsVirtual ? "loopback of " : ""));
            sb.AppendLine();
            sb.AppendLine($"      fader {FaderText(s.GainDb)}, routing [{Routing(s)}]{(s.Mute ? ", MUTE" : "")}{(s.Solo ? ", SOLO" : "")}{(s.Mono ? ", MONO" : "")}" +
                          $"{(s.Gate.Enabled ? $", gate {s.Gate.ThresholdDb:0} dB" : "")}{(s.Comp.Enabled ? $", comp {s.Comp.Ratio:0.0}:1" : "")}" +
                          $"{(s.IsVirtual && (s.EqLowDb != 0 || s.EqMidDb != 0 || s.EqHighDb != 0) ? $", EQ {s.EqLowDb:+0;-0}/{s.EqMidDb:+0;-0}/{s.EqHighDb:+0;-0} dB" : "")}");
        }
        sb.AppendLine();
        sb.AppendLine("OUTPUT BUSES");
        foreach (var b in p.Buses)
        {
            sb.Append($"  {MixerProfile.BusName(b.Index),-4} {Quote(b.Label),-16} ");
            sb.Append(DescribeDevice(b.Device, render, ""));
            sb.AppendLine();
            int activeCells = b.EqLeft.Concat(b.EqRight).Count(c => c.On && !c.IsEffectivelyFlat);
            sb.AppendLine($"      fader {FaderText(b.GainDb)}{(b.Mute ? ", MUTE" : "")}{(b.Mono ? ", MONO" : "")}, EQ {(b.EqOn ? "on" : "off")} ({activeCells} active cells)");
        }
        if (r.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("NOTES");
            foreach (var n in r.Notes) sb.AppendLine("  • " + n);
        }
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("WARNINGS");
            foreach (var w in r.Warnings) sb.AppendLine("  ! " + w);
        }
        return sb.ToString();

        static string Quote(string s) => string.IsNullOrEmpty(s) ? "" : $"\"{s}\"";
        static string FaderText(double db) => db <= -60 ? "-inf" : $"{db:+0.0;-0.0;0.0} dB";
        static string Routing(StripSettings s)
        {
            var parts = new List<string>();
            for (int i = 0; i < MixerProfile.BusCount; i++) if (s.RoutedTo(i)) parts.Add(MixerProfile.BusName(i));
            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }
    }

    private static string DescribeDevice(DeviceRef d, List<(string Id, string Name)> candidates, string prefix)
    {
        if (d.IsEmpty) return "no device";
        int idx = DeviceService.Match(candidates, d);
        return idx >= 0
            ? $"✓ {prefix}\"{candidates[idx].Name}\""
            : $"✗ {prefix}\"{d.Name}\" is not connected right now (it binds automatically when it appears)";
    }

    public void ApplyProfile(MixerProfile profile)
    {
        Engine.ReplaceProfile(profile);
        BuildChannels();
        OnPropertyChanged(nameof(ProfileName));
        RefreshDevicesAsync();
        SaveNow();
    }

    private void LoadProfileInteractive()
    {
        if (IsLocked) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load MixerLive profile",
            Filter = "MixerLive profile (*.json)|*.json|Voicemeeter settings (*.xml)|*.xml|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog() != true) return;

        if (dlg.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            ImportFromFile(dlg.FileName);
            return;
        }

        var p = ProfileStore.LoadProfile(dlg.FileName);
        if (p == null)
        {
            MessageBox.Show("That file is not a valid MixerLive profile.", "Load profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Engine settings travel in exported profiles; settings.json stays the live source of truth.
        var engine = p.Engine;
        p.Engine = null;
        if (engine != null && !engine.Matches(Settings))
        {
            var answer = MessageBox.Show(Application.Current.MainWindow,
                $"This profile also contains engine settings:\n\n    {engine.Describe()}\n\n" +
                $"You are currently using:\n\n    {EngineSettings.From(Settings).Describe()}\n\n" +
                "Apply the profile's engine settings too?\n\n" +
                "Yes: load the mixer and the engine settings (the audio restarts for a moment).\n" +
                "No: load only the mixer and keep your current engine settings.\n" +
                "Cancel: do not load anything.",
                "Load profile", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                if (!EngineSettings.StandardRates.Contains(engine.SampleRate))
                {
                    Log.Warn($"Profile rate {engine.SampleRate} Hz is not one of the selectable rates; keeping {Settings.SampleRate} Hz");
                    engine.SampleRate = Settings.SampleRate;
                }
                ApplyEngineSettings(engine, "from profile");
            }
        }

        ApplyProfile(p);
        Log.Info($"Loaded profile {dlg.FileName}");
    }

    /// <summary>Audio settings…: sample rate, buffer size, latency and resampler in one window.</summary>
    private void OpenAudioSettings()
    {
        var wnd = new AudioSettingsWindow(Engine, EngineSettings.From(Settings)) { Owner = Application.Current.MainWindow };
        if (wnd.ShowDialog() == true && wnd.Result != null) ApplyEngineSettings(wnd.Result, "from the Audio settings window");
    }

    /// <summary>About: shows version info and credits.</summary>
    private void OpenAbout()
    {
        var wnd = new AboutWindow { Owner = Application.Current.MainWindow };
        wnd.ShowDialog();
    }

    /// <summary>
    /// The one place engine settings change: saves them, hands them to the engine and restarts it
    /// once (rebuilding buffers for the new rate and block size). Does nothing when nothing changed.
    /// </summary>
    private void ApplyEngineSettings(EngineSettings wanted, string source)
    {
        if (wanted.Matches(Settings)) return;
        wanted.ApplyTo(Settings);
        wanted.ApplyTo(Engine.Options);
        ProfileStore.SaveSettings(Settings);
        RunGuarded(Engine.ApplyOptions);
        OnEngineStatusChanged();
        Log.Info($"Engine settings applied {source}: {wanted.Describe()}");
    }

    private void SaveProfileAsInteractive()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save MixerLive profile",
            Filter = "MixerLive profile (*.json)|*.json",
            FileName = (string.IsNullOrWhiteSpace(ProfileName) ? "profile" : ProfileName) + ".json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog() != true) return;
        Engine.Profile.Name = Path.GetFileNameWithoutExtension(dlg.FileName);

        // The exported file carries the engine settings too, so one file restores the whole setup.
        var export = ProfileStore.Clone(Engine.Profile);
        export.Engine = EngineSettings.From(Settings);
        ProfileStore.SaveProfile(export, dlg.FileName);
        Log.Info($"Profile exported to {dlg.FileName} ({export.Engine.Describe()})");

        SaveNow();
        OnPropertyChanged(nameof(ProfileName));
    }

    private static void OpenLogs()
    {
        try
        {
            if (Directory.Exists(Log.LogDirectory))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open log folder", ex);
        }
    }

    private void CloseVoicemeeter()
    {
        Task.Run(() => VoicemeeterProcess.TryClose(TimeSpan.FromSeconds(3))).ContinueWith(t =>
        {
            VoicemeeterRunning = VoicemeeterProcess.IsRunning();
            if (!VoicemeeterRunning) Engine.RequestRebind(force: true);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- internals ----

    private void BuildChannels()
    {
        foreach (var b in Buses) b.CloseDeviceVolume();
        Strips.Clear();
        Buses.Clear();
        foreach (var s in Engine.Profile.Strips) Strips.Add(new StripViewModel(s, this));
        foreach (var b in Engine.Profile.Buses) Buses.Add(new BusViewModel(b, this));
    }

    private async void RefreshDevicesAsync()
    {
        try
        {
            var devices = Engine.Devices;
            var (capture, render) = await Task.Run(() => (devices.GetDevices(DataFlow.Capture), devices.GetDevices(DataFlow.Render)));
            foreach (var s in Strips) s.RefreshDevices(s.IsVirtual ? render : capture);
            foreach (var b in Buses) b.RefreshDevices(render);
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing device lists failed", ex);
        }
    }

    private async void QueryRunAtLogonAsync()
    {
        try
        {
            bool installed = await Task.Run(StartupTask.IsInstalled);
            _runAtLogon = installed;
            OnPropertyChanged(nameof(RunAtLogon));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not query the logon task: {ex.Message}");
        }
    }

    private void OnEngineStatusChanged()
    {
        OnPropertyChanged(nameof(EngineRunning));
        OnPropertyChanged(nameof(EngineButtonText));
        UpdateEngineStatusText();
    }

    /// <summary>Set by the window. While it is hidden in the tray, meters and the Voicemeeter check pause.</summary>
    public bool WindowVisible { get; set; }

    private void OnUiTick()
    {
        if (_disposed) return;
        _tick++;

        if (!WindowVisible)
        {
            // Only the tray tooltip needs updating while the window is hidden.
            if (_tick % 30 == 0) UpdateEngineStatusText();
            return;
        }

        for (int i = 0; i < Strips.Count; i++) Strips[i].Tick(Engine.StripStatus(i));
        for (int i = 0; i < Buses.Count; i++) Buses[i].Tick(Engine.BusStatus(i));

        if (_tick % 15 == 0) UpdateEngineStatusText();
        if (_tick % 150 == 1) CheckVoicemeeterAsync();   // every 5 s
    }

    private void UpdateEngineStatusText()
    {
        int bound = 0, wanted = 0;
        for (int i = 0; i < MixerProfile.StripCount; i++)
        {
            if (Engine.Profile.Strips[i].Device.IsEmpty) continue;
            wanted++;
            if (Engine.StripStatus(i).IsBound) bound++;
        }
        for (int i = 0; i < MixerProfile.BusCount; i++)
        {
            if (Engine.Profile.Buses[i].Device.IsEmpty) continue;
            wanted++;
            if (Engine.BusStatus(i).IsBound) bound++;
        }

        if (Engine.IsRunning)
        {
            var o = Engine.Options;
            EngineStatusText = $"Running · {o.SampleRate / 1000.0:0.#} kHz · buffer {Engine.BlockFrames:N0} ({o.BlockMs:0.#} ms) · latency {o.Latency} · DSP {Engine.CpuLoad * 100:0.0} % · devices {bound}/{wanted}";
            EngineStatusDetail = $"Engine running: {o.Describe()}. Mixer DSP load {Engine.CpuLoad * 100:0.0} % of one core. Devices open: {bound} of {wanted}.";
        }
        else
        {
            EngineStatusText = "Engine stopped";
            EngineStatusDetail = "Engine stopped";
        }
    }

    private async void CheckVoicemeeterAsync()
    {
        if (_checkingVoicemeeter) return;
        _checkingVoicemeeter = true;
        try
        {
            VoicemeeterRunning = await Task.Run(VoicemeeterProcess.IsRunning);
        }
        catch { /* ignore */ }
        finally
        {
            _checkingVoicemeeter = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uiTimer.Stop();
        _saveTimer.Stop();
        foreach (var b in Buses) b.CloseDeviceVolume();
        Engine.Stop();
        Engine.Dispose();
    }
}
