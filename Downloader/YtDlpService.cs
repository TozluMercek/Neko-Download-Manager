using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Downloader;

public sealed class YtDlpService
{
    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _executablePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Downloader",
        "yt-dlp.exe");
    private readonly string _ffmpegPathFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Downloader",
        "ffmpeg-path.txt");
    private CookieBrowser? _lastWorkingCookieBrowser;

    public async Task<string> UpdateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken);

        var result = await RunAsync(["-U"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(GetFailureMessage(result));
        }

        return string.IsNullOrWhiteSpace(result.Output)
            ? "yt-dlp güncelleme denetimi tamamlandı."
            : result.Output.Trim();
    }

    public async Task<YtDlpFormatList> GetFormatsAsync(
        string url,
        CookieBrowser? cookiesFromBrowser = null,
        bool autoCookieFallback = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken);

        var arguments = new List<string>
        {
            "--no-warnings", "--no-playlist", "--dump-single-json", "--skip-download"
        };

        var result = await RunWithCookiesAsync(arguments, url, cookiesFromBrowser, autoCookieFallback, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(GetFailureMessage(result));
        }

        var jsonStart = result.Output.IndexOf('{');
        var jsonEnd = result.Output.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            throw new InvalidOperationException("yt-dlp biçim bilgisi döndürmedi.");
        }

        using var document = JsonDocument.Parse(result.Output[jsonStart..(jsonEnd + 1)]);
        if (!document.RootElement.TryGetProperty("formats", out var formatsElement) ||
            formatsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Bu bağlantı için indirilebilir biçim bulunamadı.");
        }

        var videoFormats = new List<YtDlpFormat>();
        var audioFormats = new List<YtDlpFormat>();

        foreach (var format in formatsElement.EnumerateArray())
        {
            var formatId = GetString(format, "format_id");
            if (string.IsNullOrWhiteSpace(formatId))
            {
                continue;
            }

            var hasVideo = !string.Equals(GetString(format, "vcodec"), "none", StringComparison.OrdinalIgnoreCase);
            var hasAudio = !string.Equals(GetString(format, "acodec"), "none", StringComparison.OrdinalIgnoreCase);

            if (hasVideo && !hasAudio)
            {
                videoFormats.Add(new YtDlpFormat(formatId, CreateVideoLabel(format)));
            }
            else if (!hasVideo && hasAudio)
            {
                audioFormats.Add(new YtDlpFormat(formatId, CreateAudioLabel(format)));
            }
        }

        return new YtDlpFormatList(videoFormats, audioFormats);
    }

    public async Task DownloadMediaAsync(
        string url,
        string destinationDirectory,
        YtDlpDownloadOptions options,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken);
        Directory.CreateDirectory(destinationDirectory);

        var arguments = new List<string> { "--newline", "--no-playlist", "--continue", "-P", destinationDirectory };
        if (!string.IsNullOrWhiteSpace(options.FormatSelector))
        {
            arguments.Add("-f");
            arguments.Add(options.FormatSelector);
        }

        var ffmpegPath = GetFfmpegPath();
        if (options.FormatSelector.Contains('+') && ffmpegPath is null)
        {
            throw new FfmpegNotFoundException();
        }

        if (ffmpegPath is not null)
        {
            arguments.Add("--ffmpeg-location");
            arguments.Add(ffmpegPath);
        }

        if (options.SubtitleMode != SubtitleDownloadMode.None)
        {
            arguments.Add("--write-subs");
            if (options.IncludeAutomaticSubtitles)
            {
                arguments.Add("--write-auto-subs");
            }

            arguments.Add("--sub-langs");
            arguments.Add(string.IsNullOrWhiteSpace(options.SubtitleLanguages) ? "all" : options.SubtitleLanguages);
            arguments.Add("--convert-subs");
            arguments.Add(options.SubtitleFormat.ToString().ToLowerInvariant());

            if (options.SubtitleMode is SubtitleDownloadMode.Embed or SubtitleDownloadMode.ExternalAndEmbed)
            {
                arguments.Add("--embed-subs");
                if (ffmpegPath is null)
                {
                    throw new FfmpegNotFoundException();
                }
            }

            if (options.SubtitleMode == SubtitleDownloadMode.ExternalAndEmbed)
            {
                arguments.Add("--keep-subs");
            }
        }

        var result = await RunWithCookiesAsync(
            arguments,
            url,
            options.CookiesFromBrowser,
            options.AutoCookieFallback,
            cancellationToken,
            progress);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(GetFailureMessage(result));
        }
    }

    public async Task<MediaMetadata> GetMediaMetadataAsync(
        string url,
        CookieBrowser? cookiesFromBrowser = null,
        bool autoCookieFallback = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken);

        var arguments = new List<string>
        {
            "--no-warnings",
            "--flat-playlist",
            "--skip-download",
            "--dump-single-json"
        };

        var result = await RunWithCookiesAsync(arguments, url, cookiesFromBrowser, autoCookieFallback, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(GetFailureMessage(result));
        }

        var jsonStart = result.Output.IndexOf('{');
        var jsonEnd = result.Output.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            throw new InvalidOperationException("Bağlantı için medya bilgisi alınamadı.");
        }

        using var document = JsonDocument.Parse(result.Output[jsonStart..(jsonEnd + 1)]);
        var root = document.RootElement;
        var entries = new List<PlaylistEntryInfo>();
        var isPlaylist = root.TryGetProperty("entries", out var entriesElement) &&
                         entriesElement.ValueKind == JsonValueKind.Array;

        if (isPlaylist)
        {
            var position = 0;
            foreach (var entry in entriesElement.EnumerateArray())
            {
                position++;
                var id = GetString(entry, "id");
                var entryUrl = GetString(entry, "webpage_url");
                if (!IsHttpUrl(entryUrl))
                {
                    entryUrl = GetString(entry, "url");
                }

                if (!IsHttpUrl(entryUrl) && !string.IsNullOrWhiteSpace(id))
                {
                    entryUrl = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(id)}";
                }

                if (string.IsNullOrWhiteSpace(entryUrl))
                {
                    continue;
                }

                entries.Add(new PlaylistEntryInfo(
                    id,
                    string.IsNullOrWhiteSpace(GetString(entry, "title")) ? $"Video {position}" : GetString(entry, "title"),
                    entryUrl,
                    GetThumbnailUrl(entry),
                    position));
            }
        }

        return new MediaMetadata(
            string.IsNullOrWhiteSpace(GetString(root, "title")) ? "Başlıksız medya" : GetString(root, "title"),
            GetThumbnailUrl(root),
            isPlaylist,
            entries,
            GetString(root, "extractor"),
            ParseUploadDate(GetString(root, "upload_date")));
    }

    public Task<byte[]> GetThumbnailAsync(string thumbnailUrl, CancellationToken cancellationToken = default)
    {
        if (!IsHttpUrl(thumbnailUrl))
        {
            throw new ArgumentException("Geçerli bir thumbnail adresi gerekli.", nameof(thumbnailUrl));
        }

        return HttpClient.GetByteArrayAsync(thumbnailUrl, cancellationToken);
    }

    public bool IsFfmpegAvailable() => GetFfmpegPath() is not null;

    public string? GetFfmpegPath()
    {
        try
        {
            if (File.Exists(_ffmpegPathFilePath))
            {
                var savedPath = File.ReadAllText(_ffmpegPathFilePath).Trim();
                if (IsFfmpegExecutable(savedPath))
                {
                    return Path.GetFullPath(savedPath);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return FindFfmpegPath();
    }

    public void SaveFfmpegPath(string ffmpegPath)
    {
        if (!IsFfmpegExecutable(ffmpegPath))
        {
            throw new ArgumentException("Geçerli bir ffmpeg.exe dosyası seçin.", nameof(ffmpegPath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_ffmpegPathFilePath)!);
        File.WriteAllText(_ffmpegPathFilePath, Path.GetFullPath(ffmpegPath));
    }

    public async Task DownloadAsync(
        string url,
        string destinationDirectory,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken);
        Directory.CreateDirectory(destinationDirectory);

        var result = await RunAsync(
            ["--newline", "-P", destinationDirectory, url],
            cancellationToken,
            progress);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(GetFailureMessage(result));
        }
    }

    public async Task DownloadFormatsAsync(
        string url,
        string videoFormatId,
        string audioFormatId,
        string destinationDirectory,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpegPath = GetFfmpegPath();
        if (ffmpegPath is null)
        {
            throw new FfmpegNotFoundException();
        }

        await EnsureInstalledAsync(cancellationToken);
        Directory.CreateDirectory(destinationDirectory);

        var result = await RunAsync(
            ["--newline", "--no-playlist", "--ffmpeg-location", ffmpegPath,
                "-f", $"{videoFormatId}+{audioFormatId}", "-P", destinationDirectory, url],
            cancellationToken,
            progress);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(GetFailureMessage(result));
        }
    }

    private async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_executablePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_executablePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _executablePath + ".download";

        try
        {
            using var response = await HttpClient.GetAsync(
                DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var file = File.Create(temporaryPath))
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                await stream.CopyToAsync(file, cancellationToken);
            }

            File.Move(temporaryPath, _executablePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IProgress<YtDlpProgress>? progress = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var outputLock = new object();

        void CaptureLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (outputLock)
            {
                output.AppendLine(line);
            }

            var percentMatch = Regex.Match(line, @"(?<percent>\d+(?:[.,]\d+)?)%");
            double? percent = null;
            if (percentMatch.Success && double.TryParse(
                    percentMatch.Groups["percent"].Value.Replace(',', '.'),
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var parsedPercent))
            {
                percent = parsedPercent;
            }

            progress?.Report(new YtDlpProgress(percent, line));
        }

        process.OutputDataReceived += (_, eventArgs) => CaptureLine(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => CaptureLine(eventArgs.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("yt-dlp işlemi başlatılamadı.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString());
    }

    private static string GetFailureMessage(ProcessResult result)
    {
        var output = result.Output.Trim();
        return string.IsNullOrWhiteSpace(output)
            ? $"yt-dlp işlemi {result.ExitCode} koduyla başarısız oldu."
            : output;
    }

    private static string? FindFfmpegPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // Giriş gerektiren içeriklerde (ör. Reddit NSFW, yaş kısıtlı videolar) çerez seçilmemişse
    // yüklü tarayıcıların çerezleri sırayla denenir; çalışan tarayıcı sonraki istekler için hatırlanır.
    private async Task<ProcessResult> RunWithCookiesAsync(
        IReadOnlyList<string> arguments,
        string url,
        CookieBrowser? browser,
        bool autoCookieFallback,
        CancellationToken cancellationToken,
        IProgress<YtDlpProgress>? progress = null)
    {
        var result = await RunAsync(BuildArguments(arguments, url, browser), cancellationToken, progress);
        if (result.ExitCode == 0 || !RequiresAuthentication(result.Output))
        {
            return result;
        }

        if (browser.HasValue || !autoCookieFallback)
        {
            return result with { Output = CreateAuthenticationMessage(browser) };
        }

        var candidates = new List<CookieBrowser> { CookieBrowser.Firefox, CookieBrowser.Edge, CookieBrowser.Chrome };
        if (_lastWorkingCookieBrowser is { } lastWorking)
        {
            candidates.Remove(lastWorking);
            candidates.Insert(0, lastWorking);
        }

        foreach (var candidate in candidates)
        {
            progress?.Report(new YtDlpProgress(null, $"Giriş gerekiyor, {candidate} çerezleri deneniyor..."));
            var retry = await RunAsync(BuildArguments(arguments, url, candidate), cancellationToken, progress);
            if (retry.ExitCode == 0)
            {
                _lastWorkingCookieBrowser = candidate;
                return retry;
            }

            if (!RequiresAuthentication(retry.Output) && !IsCookieReadFailure(retry.Output))
            {
                // Çerezler okundu ve kimlik sorunu aşıldı, ancak başka bir hata oluştu.
                return retry;
            }
        }

        return result with { Output = CreateAuthenticationMessage(null) };
    }

    private static List<string> BuildArguments(IReadOnlyList<string> arguments, string url, CookieBrowser? browser)
    {
        var allArguments = new List<string>(arguments);
        if (browser.HasValue)
        {
            allArguments.Add("--cookies-from-browser");
            allArguments.Add(browser.Value.ToString().ToLowerInvariant());
        }

        allArguments.Add(url);
        return allArguments;
    }

    private static bool RequiresAuthentication(string output)
    {
        return output.Contains("--cookies-from-browser", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("authentication is required", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("login required", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCookieReadFailure(string output)
    {
        return output.Contains("cookie", StringComparison.OrdinalIgnoreCase) &&
               (output.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("not find", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("DPAPI", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateAuthenticationMessage(CookieBrowser? browser)
    {
        var source = browser.HasValue
            ? $"{browser.Value} tarayıcısının çerezleriyle de"
            : "Tarayıcı çerezleriyle de";
        return $"Bu içerik için oturum açmak gerekiyor. {source} giriş doğrulanamadı. " +
               "Siteye tarayıcınızda giriş yapın, Ayarlar > Tarayıcı çerezleri bölümünden o tarayıcıyı seçin " +
               "ve tarayıcıyı kapatıp tekrar deneyin. (Chrome/Edge çerezleri okunamazsa Firefox önerilir.)";
    }

    private static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string? GetThumbnailUrl(JsonElement item)
    {
        var thumbnail = GetString(item, "thumbnail");
        if (IsHttpUrl(thumbnail))
        {
            return thumbnail;
        }

        if (item.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in thumbnails.EnumerateArray().Reverse())
            {
                thumbnail = GetString(candidate, "url");
                if (IsHttpUrl(thumbnail))
                {
                    return thumbnail;
                }
            }
        }

        return null;
    }

    private static bool IsFfmpegExecutable(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               string.Equals(Path.GetFileName(path), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(path);
    }

    private static string CreateVideoLabel(JsonElement format)
    {
        var details = new List<string> { $"{GetString(format, "height")}p" };
        AddDetail(details, GetString(format, "ext"));
        AddDetail(details, GetString(format, "format_note"));
        AddDetail(details, FormatSize(format));
        return string.Join(" · ", details.Where(detail => !string.IsNullOrWhiteSpace(detail)));
    }

    private static string CreateAudioLabel(JsonElement format)
    {
        var details = new List<string> { "Ses" };
        AddDetail(details, GetString(format, "ext"));
        AddDetail(details, GetString(format, "format_note"));
        AddDetail(details, GetString(format, "abr"), " kb/sn");
        AddDetail(details, FormatSize(format));
        return string.Join(" · ", details.Where(detail => !string.IsNullOrWhiteSpace(detail)));
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }

    private static string FormatSize(JsonElement format)
    {
        var size = GetString(format, "filesize");
        if (!long.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
        {
            size = GetString(format, "filesize_approx");
            if (!long.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes))
            {
                return string.Empty;
            }
        }

        var megabytes = bytes / 1024d / 1024d;
        return $"{megabytes:0.#} MB";
    }

    private static void AddDetail(ICollection<string> details, string value, string suffix = "")
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            details.Add(value + suffix);
        }
    }

    private static DateTime? ParseUploadDate(string uploadDateStr)
    {
        if (string.IsNullOrWhiteSpace(uploadDateStr) || uploadDateStr.Length != 8)
            return null;

        if (DateTime.TryParseExact(uploadDateStr, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            return date;

        return null;
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}

public sealed record YtDlpProgress(double? Percent, string Message);
public sealed record YtDlpFormat(string FormatId, string DisplayName);
public sealed record YtDlpFormatList(
    IReadOnlyList<YtDlpFormat> VideoFormats,
    IReadOnlyList<YtDlpFormat> AudioFormats);

public sealed class FfmpegNotFoundException : Exception
{
    public FfmpegNotFoundException()
        : base("FFmpeg bulunamadı.")
    {
    }
}
