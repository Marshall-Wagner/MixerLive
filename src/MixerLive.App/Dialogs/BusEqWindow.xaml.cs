using System.IO;
using System.Windows;
using MixerLive.App.ViewModels;
using MixerLive.Core.Logging;

namespace MixerLive.App.Dialogs;

/// <summary>Parametric EQ editor, shared by hardware strips and buses (DataContext = <see cref="EqEditorViewModel"/>).</summary>
public partial class BusEqWindow : Window
{
    public BusEqWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Eq?.RefreshMonoNote();
            string? active = Eq?.ActiveEqualizerApoConfig;
            ImportActiveButton.IsEnabled = active != null;
            ImportActiveButton.ToolTip = active != null
                ? $"Imports {active} (with its Include files)"
                : "Equalizer APO is not installed on this PC. You don't need it: load your EQ with \"Import EQ file (.txt)…\".";
        };
    }

    private EqEditorViewModel? Eq => DataContext as EqEditorViewModel;

    private void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (Eq == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import EQ file (.txt)",
            Filter = "EQ filter files (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != true) return;
        Import(dlg.FileName);
    }

    private void ImportActive_Click(object sender, RoutedEventArgs e)
    {
        string? path = Eq?.ActiveEqualizerApoConfig;
        if (path != null) Import(path);
    }

    private void Import(string path)
    {
        try
        {
            Eq!.ImportEqFile(path);
            Log.Info($"{Eq.Title}: EQ import from {path}: {Eq.EqInfo}");
        }
        catch (Exception ex)
        {
            Log.Error($"EQ import from '{path}' failed", ex);
            MessageBox.Show(this, $"Could not import {Path.GetFileName(path)}:\n{ex.Message}", "EQ import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyLeftToRight_Click(object sender, RoutedEventArgs e) => Eq?.CopyLeftEqToRight();

    private void Reset_Click(object sender, RoutedEventArgs e) => Eq?.ResetEq();

    private void AddLeft_Click(object sender, RoutedEventArgs e) => Eq?.AddCell(0);

    private void AddRight_Click(object sender, RoutedEventArgs e) => Eq?.AddCell(1);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
