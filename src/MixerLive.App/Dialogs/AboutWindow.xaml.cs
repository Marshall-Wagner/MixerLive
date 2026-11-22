using System;
using System.Reflection;
using System.Windows;

namespace MixerLive.App.Dialogs;

/// <summary>
/// Interaction logic for AboutWindow.xaml — shows app name, version, features list, and credits.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        LoadVersionInfo();
    }

    private void LoadVersionInfo()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;

            // Build a clean version string: Major.Minor.Patch (e.g., "0.1.0")
            VersionText.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";

            // Also update the window title with version
            Title = $"About MixerLive — v{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception ex)
        {
            VersionText.Text = "Version information unavailable";
            Log.Warn($"Could not read assembly version info: {ex.Message}");
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
