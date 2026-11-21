using System.Windows;

namespace MixerLive.App.Dialogs;

public partial class ImportPreviewWindow : Window
{
    public ImportPreviewWindow(string path, string summary)
    {
        InitializeComponent();
        PathRun.Text = path;
        SummaryBox.Text = summary;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
