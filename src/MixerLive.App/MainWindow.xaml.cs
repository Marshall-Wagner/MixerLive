using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MixerLive.App.ViewModels;
using MixerLive.Core.Audio;

namespace MixerLive.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnViewModelPropertyChanged;
            if (e.NewValue is MainViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
                vm.WindowVisible = IsVisible;
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.WindowVisible = IsVisible;
        };
    }

    private bool Locked => DataContext is MainViewModel { IsLocked: true };

    /// <summary>When the mixer locks while a fader or box has keyboard focus, move focus away so arrow keys cannot change it.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLocked) && Locked && MixerArea.IsKeyboardFocusWithin)
            LockButton.Focus();
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting && App.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!App.IsExiting) ((App)Application.Current).ExitApplication();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && App.Settings.MinimizeToTray)
        {
            Hide();
            WindowState = WindowState.Normal;
        }
    }

    // ---- fader and volume boxes ----

    private static bool Fine => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    private void Fader_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && !Locked)
        {
            slider.Value = 0;
            e.Handled = true;
        }
    }

    /// <summary>Wheel over a fader: ±1 dB per notch, Ctrl for ±0.1 dB. Handled so the mixer does not scroll.</summary>
    private void Fader_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Locked) return;
        if (sender is FrameworkElement { DataContext: IGainControl g })
        {
            g.GainDb = VolumeScale.StepDb(g.GainDb, Math.Sign(e.Delta), Fine ? 0.1 : 1.0);
            e.Handled = true;
        }
    }

    /// <summary>Wheel over the device-volume slider: ±2 % per notch, Ctrl for ±0.5 %.</summary>
    private void DeviceVolume_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Locked) return;
        if (sender is FrameworkElement { DataContext: BusViewModel bus })
        {
            bus.StepDeviceVolume(Math.Sign(e.Delta), Fine);
            e.Handled = true;
        }
    }

    private void GainBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is TextBox box && Step(box, Math.Sign(e.Delta))) e.Handled = true;
    }

    private void GainBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (Locked)
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                box.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                box.SelectAll();
                e.Handled = true;
                break;
            case Key.Up:
                e.Handled = Step(box, +1);
                break;
            case Key.Down:
                e.Handled = Step(box, -1);
                break;
        }
    }

    private void GainBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.Dispatcher.BeginInvoke(new Action(box.SelectAll));
    }

    /// <summary>dB box: ±1 dB (Ctrl ±0.1). % and device boxes: ±2 % (Ctrl ±0.5), like the Windows volume flyout.</summary>
    private bool Step(TextBox box, int direction)
    {
        if (direction == 0 || Locked) return false;
        if (box.Tag as string == "dev" && box.DataContext is BusViewModel bus)
        {
            bus.StepDeviceVolume(direction, Fine);
            box.SelectAll();
            return true;
        }
        if (box.DataContext is not IGainControl g) return false;
        g.GainDb = box.Tag as string == "pct"
            ? VolumeScale.StepPercent(g.GainDb, direction, Fine ? 0.5 : 2.0)
            : VolumeScale.StepDb(g.GainDb, direction, Fine ? 0.1 : 1.0);
        box.SelectAll();
        return true;
    }
}
