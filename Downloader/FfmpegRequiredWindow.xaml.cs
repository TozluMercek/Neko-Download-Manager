using System.Diagnostics;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Navigation;

namespace Downloader;

public partial class FfmpegRequiredWindow : Window
{
    private readonly YtDlpService _ytDlpService;

    public FfmpegRequiredWindow(YtDlpService ytDlpService)
    {
        _ytDlpService = ytDlpService;
        InitializeComponent();
    }

    private void ChooseFfmpegButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "ffmpeg.exe dosyasını seçin",
            Filter = "FFmpeg (ffmpeg.exe)|ffmpeg.exe",
            FileName = "ffmpeg.exe",
            CheckFileExists = true,
            CheckPathExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _ytDlpService.SaveFfmpegPath(dialog.FileName);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "FFmpeg yolu kaydedilemedi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DownloadLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
