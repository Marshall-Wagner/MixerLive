using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MixerLive.App.Dialogs;
using MixerLive.App.Tray;
using MixerLive.App.ViewModels;
using MixerLive.Core.Audio;
using MixerLive.Core.Import;
using MixerLive.Core.Logging;
using MixerLive.Core.Model;
using NAudio.CoreAudioApi;

namespace MixerLive.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private MainViewModel? _vm;
    private DeviceService? _devices;

    public static bool IsExiting { get; private set; }
    public static AppSettings Settings { get; private set; } = new();

    private sealed class StartupArgs
    {
        public bool Minimized;
        public int SelfTestSeconds;
        public string? ImportPath;
        public string? ProfilePath;
        public int? SampleRate;
        public int? OpenEqBus;
        public ResampleQuality? Resampler;
        public double? BlockMs;
        public bool TestInstance;
        public string? SnapshotPath;
        public bool Lock;
        public bool OpenAudioSettings;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Log.Init();

        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Error("Unobserved task exception", a.Exception);
            a.SetObserved();
        };
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var args = ParseArgs(e.Args);

        if (args.SelfTestSeconds > 0)
        {
            int code = RunSelfTest(args);
            Shutdown(code);
            return;
        }

        bool snapshot = args.SnapshotPath != null;
        if (args.TestInstance || snapshot)
        {
            // Runs next to the real mixer without the single-instance lock and without saving.
            ProfileStore.ReadOnly = true;
            Log.Info(snapshot ? $"Snapshot session → {args.SnapshotPath}" : "Test instance: profile and settings are read-only");
        }
        else
        {
            _mutex = new Mutex(true, @"Local\MixerLive.SingleInstance", out bool isFirstInstance);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MixerLive.ShowWindow");
            if (!isFirstInstance)
            {
                _showEvent.Set();
                Log.Info("Another instance is already running; asked it to show its window.");
                Shutdown();
                return;
            }
        }

        Settings = ProfileStore.LoadSettings();
        ApplyOverrides(Settings, args);   // session-only unless changed in the UI
        var profile = LoadInitialProfile(args, out string? importMessage);

        _devices = new DeviceService();
        var engine = new AudioEngine(profile, EngineOptions.From(EngineSettings.From(Settings)), _devices);

        _vm = new MainViewModel(engine, Settings);
        _vm.ExitRequested += ExitApplication;

        var window = new MainWindow { DataContext = _vm };
        MainWindow = window;
        _vm.ShowRequested += window.ShowAndActivate;

        if (snapshot)
        {
            RunSnapshot(window, engine, args.SnapshotPath!, args.OpenAudioSettings);
            return;
        }

        _tray = new TrayIcon(_vm);
        if (_showEvent != null) StartShowListener();

        if (args.Minimized) Log.Info("Started minimized (tray only)");
        else window.Show();

        if (Settings.AutoStartEngine) engine.Start();

        if (importMessage != null && !args.Minimized)
            MessageBox.Show(window, importMessage, "MixerLive – import", MessageBoxButton.OK, MessageBoxImage.Information);

        if (!Settings.FirstRunDone && !args.Minimized && importMessage == null)
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _vm.OfferFirstRunImport()));

        if (args.OpenEqBus is int eqBus && !args.Minimized)
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _vm.OpenBusEq(_vm.Buses[eqBus])));
    }

    /// <summary>
    /// --snapshot: shows the window far off-screen (never on the user's desktop), runs the engine
    /// for a few seconds so statuses and meters are live, renders the window to a PNG and exits.
    /// </summary>
    private void RunSnapshot(MainWindow window, AudioEngine engine, string path, bool withAudioSettings)
    {
        ShowOffScreen(window);
        engine.Start();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Log.Info($"Snapshot hit test: {HitTestReport(window)}");
                RenderWindow(window, path);
                if (withAudioSettings)
                {
                    var adv = new AudioSettingsWindow(engine, EngineSettings.From(Settings)) { Owner = window };
                    ShowOffScreen(adv);
                    RenderWindow(adv, Path.ChangeExtension(path, ".audio-settings.png"));
                    adv.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Snapshot failed", ex);
            }
            ExitApplication();
        };
        timer.Start();
    }

    private static void ShowOffScreen(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000;
        w.Top = -32000;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Show();
        w.UpdateLayout();
    }

    private static void RenderWindow(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        double width = root.ActualWidth + root.Margin.Left + root.Margin.Right;
        double height = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        // Paint through a brush so the root's margin offset is kept and nothing is clipped.
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top },
                null, new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(dv);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        png.Save(fs);
        Log.Info($"Snapshot written to {path}");
    }

    /// <summary>Snapshot diagnostics: does a mouse press on the first fader reach it (it must not while locked)?</summary>
    private static string HitTestReport(MainWindow window)
    {
        var fader = FindDescendant<System.Windows.Controls.Slider>(window.MixerArea,
            s => s.IsVisible && s.Orientation == System.Windows.Controls.Orientation.Vertical);
        if (fader == null) return "no fader found";
        // Aim at the thumb (the part you drag); the empty track around it is not clickable anyway.
        var thumb = FindDescendant<System.Windows.Controls.Primitives.Thumb>(fader);
        if (thumb == null) return "no fader thumb found";
        var center = thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), window);
        var hit = window.InputHitTest(center) as DependencyObject;
        bool reaches = false;
        for (var d = hit; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, fader))
            {
                reaches = true;
                break;
            }
        }
        return $"locked={Settings.Locked}, mouse on a fader reaches it={reaches} (hit {hit?.GetType().Name ?? "nothing"}), " +
               $"Tab into mixer={System.Windows.Input.KeyboardNavigation.GetTabNavigation(window.MixerArea)}";
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match && (predicate == null || predicate(match))) return match;
            var deeper = FindDescendant(child, predicate);
            if (deeper != null) return deeper;
        }
        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        Log.Info("---- MixerLive exited ----");
        base.OnExit(e);
    }

    /// <summary>Full shutdown (tray Exit, or window close when close-to-tray is off).</summary>
    public void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;
        try { _vm?.SaveNow(); } catch (Exception ex) { Log.Error("Save on exit failed", ex); }
        try { _vm?.Dispose(); } catch (Exception ex) { Log.Error("Engine shutdown failed", ex); }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _devices?.Dispose(); } catch { /* ignore */ }
        try { MainWindow?.Close(); } catch { /* ignore */ }
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show($"Something went wrong, but the audio engine keeps running.\n\n{e.Exception.Message}\n\nDetails were written to the log.",
                "MixerLive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* ignore */ }
    }

    private static StartupArgs ParseArgs(string[] args)
    {
        var r = new StartupArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            switch (a)
            {
                case "--minimized":
                case "-m":
                case "/minimized":
                    r.Minimized = true;
                    break;
                case "--selftest":
                    r.SelfTestSeconds = 8;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int s))
                    {
                        r.SelfTestSeconds = Math.Max(1, s);
                        i++;
                    }
                    break;
                case "--import":
                    if (i + 1 < args.Length)
                    {
                        r.ImportPath = args[i + 1];
                        i++;
                    }
                    break;
                case "--profile":
                    if (i + 1 < args.Length)
                    {
                        r.ProfilePath = args[i + 1];
                        i++;
                    }
                    break;
                case "--rate":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int hz) && hz is >= 8000 and <= 768000)
                    {
                        r.SampleRate = hz;
                        i++;
                    }
                    break;
                case "--resampler":
                    if (i + 1 < args.Length && Enum.TryParse<ResampleQuality>(args[i + 1], ignoreCase: true, out var q))
                    {
                        r.Resampler = q;
                        i++;
                    }
                    break;
                case "--block":
                    if (i + 1 < args.Length
                        && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms)
                        && ms >= AdvancedEngineSettings.MinBlockMs && ms <= AdvancedEngineSettings.MaxBlockMs)
                    {
                        r.BlockMs = ms;
                        i++;
                    }
                    break;
                case "--test-instance":
                    r.TestInstance = true;
                    break;
                case "--lock":
                    r.Lock = true;
                    break;
                case "--snapshot": // debug: render the window off-screen to a PNG and exit (implies --test-instance)
                    if (i + 1 < args.Length)
                    {
                        r.SnapshotPath = args[i + 1];
                        i++;
                    }
                    break;
                case "--open-audio-settings": // debug: with --snapshot, also render the Audio settings window to <file>.audio-settings.png
                    r.OpenAudioSettings = true;
                    break;
                case "--open-eq": // debug: open the EQ window of bus N (0..4) after start
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int bus) && bus is >= 0 and < MixerProfile.BusCount)
                    {
                        r.OpenEqBus = bus;
                        i++;
                    }
                    break;
            }
        }
        return r;
    }

    /// <summary>Command-line overrides of the saved settings (kept only if the user later changes something in the UI).</summary>
    private static void ApplyOverrides(AppSettings settings, StartupArgs args)
    {
        settings.Advanced?.Normalize();
        if (args.SampleRate is int rate) settings.SampleRate = rate;
        if (args.Resampler is ResampleQuality rq) settings.Resampler = rq;
        if (args.BlockMs is double block)
        {
            settings.Advanced ??= new AdvancedEngineSettings();
            settings.Advanced.BlockMs = block;
        }
        if (args.Lock) settings.Locked = true;
    }

    private static MixerProfile LoadInitialProfile(StartupArgs args, out string? message)
    {
        message = null;
        if (!string.IsNullOrWhiteSpace(args.ImportPath))
        {
            try
            {
                var result = VoicemeeterXmlImporter.ImportFile(args.ImportPath);
                ProfileStore.SaveProfile(result.Profile);
                Settings.FirstRunDone = true;
                ProfileStore.SaveSettings(Settings);
                message = $"Imported Voicemeeter settings from:\n{args.ImportPath}\n\n" +
                          string.Join("\n", result.Notes.Concat(result.Warnings.Select(w => "Warning: " + w)));
                Log.Info($"Imported profile from {args.ImportPath}");
                return result.Profile;
            }
            catch (Exception ex)
            {
                Log.Error($"Import of '{args.ImportPath}' failed", ex);
                message = $"Could not import {args.ImportPath}: {ex.Message}";
            }
        }
        if (!string.IsNullOrWhiteSpace(args.ProfilePath))
        {
            var p = ProfileStore.LoadProfile(args.ProfilePath);
            if (p != null)
            {
                Log.Info($"Loaded profile from {args.ProfilePath}");
                return p;
            }
            message = $"Could not load profile {args.ProfilePath}; using the saved profile instead.";
        }
        return ProfileStore.LoadProfile() ?? MixerProfile.CreateDefault();
    }

    private void StartShowListener()
    {
        var t = new Thread(() =>
        {
            while (!IsExiting)
            {
                try
                {
                    if (_showEvent!.WaitOne(1000))
                        Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.ShowAndActivate()));
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "show-window listener"
        };
        t.Start();
    }

    // ---- --selftest: run the engine headless for a few seconds and print a report ----

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    private static int RunSelfTest(StartupArgs args)
    {
        AttachConsole(-1);
        TextWriter console;
        try { console = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }; }
        catch { console = TextWriter.Null; }

        void Out(string s)
        {
            console.WriteLine(s);
            foreach (var line in s.Split('\n'))
                if (line.Trim().Length > 0) Log.Info("[selftest] " + line.TrimEnd());
        }

        try
        {
            Settings = ProfileStore.LoadSettings();
            ApplyOverrides(Settings, args);   // test-only overrides, never saved
            var profile = LoadInitialProfile(args, out string? importMessage);
            if (importMessage != null) Out(importMessage);
            Out($"Profile: {profile.Name}{(profile.SourceFile != null ? $" (from {profile.SourceFile})" : "")}");

            using var devices = new DeviceService();
            Out("Capture devices:");
            foreach (var d in devices.GetDevices(DataFlow.Capture)) Out("  " + d.Name);
            Out("Render devices:");
            foreach (var d in devices.GetDevices(DataFlow.Render)) Out("  " + d.Name);

            using var engine = new AudioEngine(profile, EngineOptions.From(EngineSettings.From(Settings)), devices);
            engine.Start();
            Out($"Engine running for {args.SelfTestSeconds} s ...");
            Thread.Sleep(args.SelfTestSeconds * 1000);

            string report = engine.Report();
            engine.Stop();
            Out(report);

            string reportPath = Path.Combine(ProfileStore.AppDataDirectory, "selftest-report.txt");
            try
            {
                Directory.CreateDirectory(ProfileStore.AppDataDirectory);
                File.WriteAllText(reportPath, report);
                Out("Report written to " + reportPath);
            }
            catch { /* ignore */ }
            return 0;
        }
        catch (Exception ex)
        {
            Out("Self-test failed: " + ex);
            return 1;
        }
    }
}
