using System.Windows;
using MixerLive.App.ViewModels;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace MixerLive.App.Tray;

/// <summary>Notification-area icon with a small menu; the window hides to it instead of closing.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly SD.Icon _image;
    private readonly WF.ToolStripMenuItem _engineItem;
    private readonly WF.ToolStripMenuItem _logonItem;
    private readonly MainViewModel _vm;

    public TrayIcon(MainViewModel vm)
    {
        _vm = vm;
        _image = LoadTrayIcon();

        var menu = new WF.ContextMenuStrip();
        var show = new WF.ToolStripMenuItem("Show mixer");
        show.Font = new SD.Font(show.Font, SD.FontStyle.Bold);
        show.Click += (_, _) => vm.ShowWindow();

        _engineItem = new WF.ToolStripMenuItem("Stop engine");
        _engineItem.Click += (_, _) => vm.ToggleEngineCommand.Execute(null);

        var restart = new WF.ToolStripMenuItem("Restart audio engine");
        restart.Click += (_, _) => vm.RestartEngineCommand.Execute(null);

        _logonItem = new WF.ToolStripMenuItem("Run at Windows sign-in") { CheckOnClick = true };
        _logonItem.Click += (_, _) => vm.RunAtLogon = _logonItem.Checked;

        var lockItem = new WF.ToolStripMenuItem("Lock settings") { CheckOnClick = true };
        lockItem.Click += (_, _) => vm.IsLocked = lockItem.Checked;

        var exit = new WF.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => vm.RequestExit();

        menu.Items.AddRange(new WF.ToolStripItem[]
        {
            show, new WF.ToolStripSeparator(),
            _engineItem, restart, new WF.ToolStripSeparator(),
            lockItem, _logonItem, new WF.ToolStripSeparator(),
            exit
        });
        menu.Opening += (_, _) =>
        {
            _engineItem.Text = vm.EngineRunning ? "Stop engine" : "Start engine";
            _logonItem.Checked = vm.RunAtLogon;
            lockItem.Checked = vm.IsLocked;
            // While locked, only harmless actions stay available (show, restart, unlock, exit).
            _engineItem.Enabled = !vm.IsLocked;
            _logonItem.Enabled = !vm.IsLocked;
        };

        _icon = new WF.NotifyIcon
        {
            Icon = _image,
            Text = "MixerLive",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) vm.ShowWindow();
        };

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.EngineStatusText)) UpdateText();
        };
    }

    private void UpdateText()
    {
        string t = "MixerLive · " + _vm.EngineStatusText;
        if (t.Length > 120) t = t[..120];
        try { _icon.Text = t; } catch { /* ignore */ }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    /// <summary>
    /// The app icon (app.ico, hand-drawn sizes 16–256 px) at the size Windows uses in the notification
    /// area: 16 px at 100 % display scaling, 20 px at 125 %, 24 px at 150 %, 32 px at 200 %.
    /// </summary>
    private static SD.Icon LoadTrayIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))
                   ?? throw new InvalidOperationException("app.ico resource is missing");
        using var stream = info.Stream;
        return new SD.Icon(stream, WF.SystemInformation.SmallIconSize);
    }
}
