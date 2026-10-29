namespace Downloader;

public sealed record PlaylistEntryInfo(
    string Id,
    string Title,
    string Url,
    string? ThumbnailUrl,
    int Position);

public sealed record MediaMetadata(
    string Title,
    string? ThumbnailUrl,
    bool IsPlaylist,
    IReadOnlyList<PlaylistEntryInfo> Entries,
    string? Platform = null,
    DateTime? UploadDate = null);

public enum CookieBrowser
{
    Edge,
    Chrome,
    Firefox
}

public enum SubtitleDownloadMode
{
    None,
    External,
    Embed,
    ExternalAndEmbed
}

public enum SubtitleOutputFormat
{
    Srt,
    Vtt,
    Ass
}

public sealed record YtDlpDownloadOptions
{
    public string FormatSelector { get; init; } = "bv*+ba/b";
    public SubtitleDownloadMode SubtitleMode { get; init; }
    public SubtitleOutputFormat SubtitleFormat { get; init; } = SubtitleOutputFormat.Srt;
    public string SubtitleLanguages { get; init; } = "all";
    public bool IncludeAutomaticSubtitles { get; init; }
    public CookieBrowser? CookiesFromBrowser { get; init; }
}
