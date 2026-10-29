using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Downloader;

public sealed class PlaylistEntrySelection(PlaylistEntryInfo entry) : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public PlaylistEntryInfo Entry { get; } = entry;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DownloadQueueItem : INotifyPropertyChanged
{
    private string _status = "Sırada";
    private double _progress;
    private string _details = "Bekliyor";
    private string? _downloadDirectory;
    private CancellationTokenSource _cancellation = new();
    private bool _isProcessing;
    private bool _resumeRequested;

    public DownloadQueueItem(string url, string title, string? thumbnailUrl, YtDlpDownloadOptions options)
    {
        Url = url;
        Title = title;
        ThumbnailUrl = thumbnailUrl;
        Options = options;
    }

    public string? DownloadDirectory
    {
        get => _downloadDirectory;
        set => SetField(ref _downloadDirectory, value);
    }

    public string Url { get; }
    public string Title { get; }
    public string? ThumbnailUrl { get; }
    public YtDlpDownloadOptions Options { get; }
    public CancellationTokenSource Cancellation => _cancellation;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetField(ref _isProcessing, value);
    }
    public bool ResumeRequested
    {
        get => _resumeRequested;
        set => SetField(ref _resumeRequested, value);
    }

    public void ResetCancellation()
    {
        if (_cancellation.IsCancellationRequested)
        {
            _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string Details
    {
        get => _details;
        set => SetField(ref _details, value);
    }

    public bool CanCancel => Status is "Sırada" or "İndiriliyor";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
