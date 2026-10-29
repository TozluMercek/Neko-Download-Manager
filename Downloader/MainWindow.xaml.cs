using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;
using Microsoft.Win32;

namespace Downloader
{
    public partial class MainWindow : Window
    {
        private readonly YtDlpService _ytDlpService = new();
        private readonly string _settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Downloader");
        private readonly string _settingsFilePath;
        private readonly string _concurrencyFilePath;
        private readonly string _completedHistoryFilePath;
        private readonly ObservableCollection<DownloadQueueItem> _queue = [];
        private readonly ObservableCollection<DownloadQueueItem> _completedQueue = [];
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(6) };
        private string _downloadDirectory;
        private string? _formatsUrl;
        private MediaMetadata? _metadata;
        private bool _formatsLoaded;
        private bool _queueRunnerStarted;
        private bool _settingsReady;
        private int _notificationVersion;
        private int _maxConcurrentDownloads = 2;

        public MainWindow()
        {
            InitializeComponent();
            _settingsFilePath = Path.Combine(_settingsDirectory, "download-directory.txt");
            _concurrencyFilePath = Path.Combine(_settingsDirectory, "download-concurrency.txt");
            _completedHistoryFilePath = Path.Combine(_settingsDirectory, "completed-downloads.json");
            _downloadDirectory = LoadDownloadDirectory();
            DownloadDirectoryTextBox.Text = _downloadDirectory;
            QueueItemsControl.ItemsSource = _queue;
            CompletedItemsControl.ItemsSource = _completedQueue;
            LoadCompletedHistory();
            _maxConcurrentDownloads = LoadConcurrency();
            ConcurrencyComboBox.SelectedIndex = _maxConcurrentDownloads - 1;
            _settingsReady = true;
            _notificationTimer.Tick += (_, _) =>
            {
                HideNotification();
            };
            UpdateQueueStatus();
        }

        private void DownloadsNavButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadsPage.Visibility = Visibility.Visible;
            CompletedPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            DownloadsNavButton.Background = new SolidColorBrush(Color.FromRgb(38, 51, 77));
            DownloadsNavButton.Foreground = Brushes.White;
            CompletedNavButton.Background = Brushes.Transparent;
            CompletedNavButton.Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 215));
            SettingsNavButton.Background = Brushes.Transparent;
            SettingsNavButton.Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 215));
        }

        private void CompletedNavButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadsPage.Visibility = Visibility.Collapsed;
            CompletedPage.Visibility = Visibility.Visible;
            SettingsPage.Visibility = Visibility.Collapsed;
            DownloadsNavButton.Background = Brushes.Transparent;
            DownloadsNavButton.Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 215));
            CompletedNavButton.Background = new SolidColorBrush(Color.FromRgb(38, 51, 77));
            CompletedNavButton.Foreground = Brushes.White;
            SettingsNavButton.Background = Brushes.Transparent;
            SettingsNavButton.Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 215));
        }

        private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadsPage.Visibility = Visibility.Collapsed;
            CompletedPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
            DownloadsNavButton.Background = Brushes.Transparent;
            DownloadsNavButton.Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 215));
            CompletedNavButton.Background = Brushes.Transparent;
            CompletedNavButton.Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 215));
            SettingsNavButton.Background = new SolidColorBrush(Color.FromRgb(38, 51, 77));
            SettingsNavButton.Foreground = Brushes.White;
        }

        private void BrowseDownloadDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "İndirme konumunu seçin", InitialDirectory = _downloadDirectory, Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            _downloadDirectory = dialog.FolderName;
            DownloadDirectoryTextBox.Text = _downloadDirectory;
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                File.WriteAllText(_settingsFilePath, _downloadDirectory);
                SettingsStatusText.Text = "Kaydetme konumu kaydedildi.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SettingsStatusText.Text = $"Konum seçildi ancak ayar kaydedilemedi: {exception.Message}";
            }
        }

        private void ConcurrencyComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_settingsReady) return;
            if (ConcurrencyComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item || item.Tag is null) return;
            if (!int.TryParse(item.Tag.ToString(), out var value)) return;
            _maxConcurrentDownloads = value;
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                File.WriteAllText(_concurrencyFilePath, value.ToString());
                SettingsStatusText.Text = $"Eşzamanlı indirme limiti: {value}.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SettingsStatusText.Text = $"Limit uygulanıyor ancak kaydedilemedi: {exception.Message}";
            }
            if (_queueRunnerStarted) _queueSignal.Release();
        }

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            StatusText.Text = "yt-dlp güncellemesi denetleniyor...";
            try
            {
                var result = await _ytDlpService.UpdateAsync();
                StatusText.Text = result;
                ShowNotification(result);
            }
            catch (Exception exception)
            {
                StatusText.Text = "Güncelleme denetimi başarısız.";
                ShowNotification(exception.Message);
            }
            finally { CheckUpdateButton.IsEnabled = true; }
        }

        private async void LoadFormatsButton_Click(object sender, RoutedEventArgs e)
        {
            var urls = GetUrls();
            if (urls.Count == 0)
            {
                StatusText.Text = "Geçerli HTTP veya HTTPS bağlantısı girin.";
                return;
            }
            if (urls.Count > 1)
            {
                _metadata = null;
                _formatsLoaded = false;
                PreviewPanel.Visibility = Visibility.Collapsed;
                VideoFormatComboBox.Items.Clear();
                AudioFormatComboBox.Items.Clear();
                MediaOptionsPanel.Visibility = Visibility.Visible;
                StatusText.Text = $"{urls.Count} bağlantı kuyruğa eklenmeye hazır. Ekle düğmesini kullanın.";
                return;
            }

            var url = urls[0];
            _formatsLoaded = false;
            _formatsUrl = url;
            SetInputBusy(true);
            StatusText.Text = "Medya bilgileri alınıyor...";
            try
            {
                _metadata = await _ytDlpService.GetMediaMetadataAsync(url);
                PreviewTitleText.Text = _metadata.Title;

                var details = new List<string>();
                if (!string.IsNullOrWhiteSpace(_metadata.Platform))
                    details.Add(_metadata.Platform);
                if (_metadata.UploadDate.HasValue)
                    details.Add(_metadata.UploadDate.Value.ToString("dd MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")));

                PreviewDetailsText.Text = details.Count > 0 ? string.Join(" · ", details) : "Tek medya";
                PreviewPanel.Visibility = Visibility.Visible;
                PreviewImage.Source = null;
                if (!string.IsNullOrWhiteSpace(_metadata.ThumbnailUrl))
                {
                    var bytes = await _ytDlpService.GetThumbnailAsync(_metadata.ThumbnailUrl);
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    PreviewImage.Source = bitmap;
                }

                var formats = await _ytDlpService.GetFormatsAsync(url);
                VideoFormatComboBox.Items.Clear();
                foreach (var format in formats.VideoFormats)
                {
                    VideoFormatComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem 
                    { 
                        Content = format.DisplayName, 
                        Tag = format.FormatId 
                    });
                }
                if (VideoFormatComboBox.Items.Count > 0) VideoFormatComboBox.SelectedIndex = 0;

                AudioFormatComboBox.Items.Clear();
                foreach (var format in formats.AudioFormats)
                {
                    AudioFormatComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem 
                    { 
                        Content = format.DisplayName, 
                        Tag = format.FormatId 
                    });
                }
                if (AudioFormatComboBox.Items.Count > 0) AudioFormatComboBox.SelectedIndex = 0;

                MediaOptionsPanel.Visibility = Visibility.Visible;
                _formatsLoaded = true;
                StatusText.Text = "Önizleme hazır. Format seçin ve kuyruğa ekleyin.";
            }
            catch (Exception exception)
            {
                _metadata = null;
                StatusText.Text = "Medya bilgileri alınamadı.";
                ShowNotification(exception.Message);
            }
            finally { SetInputBusy(false); }
        }

        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var urls = GetUrls();
            if (urls.Count == 0)
            {
                StatusText.Text = "Geçerli HTTP veya HTTPS bağlantısı girin.";
                return;
            }
            var options = BuildOptions();

            if (urls.Count == 1 && _metadata is not null && string.Equals(urls[0], _formatsUrl, StringComparison.Ordinal))
                _queue.Add(new DownloadQueueItem(urls[0], _metadata.Title, _metadata.ThumbnailUrl, options));
            else
                foreach (var url in urls) _queue.Add(new DownloadQueueItem(url, GetHostTitle(url), null, options));

            _formatsLoaded = false;
            StatusText.Text = $"Kuyrukta {_queue.Count} görev var.";
            _queueSignal.Release();
            StartQueueRunner();
        }

        private void StartQueueRunner()
        {
            if (_queueRunnerStarted) return;
            _queueRunnerStarted = true;
            _ = RunQueueAsync();
        }

        private async Task RunQueueAsync()
        {
            var active = new HashSet<Task>();
            try
            {
                while (true)
                {
                    active.RemoveWhere(task => task.IsCompleted);
                    while (active.Count < _maxConcurrentDownloads)
                    {
                        var next = _queue.FirstOrDefault(item => item.Status == "Sırada");
                        if (next is null) break;
                        next.Status = "İndiriliyor";
                        active.Add(ProcessQueueItemAsync(next));
                    }
                    if (active.Count == 0 && !_queue.Any(item => item.Status == "Sırada")) break;
                    var signal = _queueSignal.WaitAsync();
                    if (active.Count > 0) await Task.WhenAny(Task.WhenAny(active), signal);
                    else await signal;
                }
            }
            finally { _queueRunnerStarted = false; }
        }

        private async Task ProcessQueueItemAsync(DownloadQueueItem item)
        {
            item.IsProcessing = true;
            try
            {
                var progress = new Progress<YtDlpProgress>(update =>
                {
                    if (update.Percent.HasValue) item.Progress = Math.Clamp(update.Percent.Value, 0, 100);
                    item.Details = update.Message;
                });
                await _ytDlpService.DownloadMediaAsync(item.Url, _downloadDirectory, item.Options, progress, item.Cancellation.Token);
                item.Progress = 100;
                item.Status = "Tamamlandı";
                item.Details = $"Kaydedildi: {_downloadDirectory}";
                item.DownloadDirectory = _downloadDirectory;
                _queue.Remove(item);
                _completedQueue.Insert(0, item);
                SaveCompletedHistory();
                ShowNotification($"İndirme tamamlandı: {item.Title}");
            }
            catch (OperationCanceledException)
            {
                if (item.Status == "Duraklatıldı")
                {
                    item.Details = "Duraklatıldı. Devam ettiğinizde indirme sürdürülecek.";
                }
                else
                {
                    item.Status = "İptal edildi";
                    item.Details = "İndirme kullanıcı tarafından iptal edildi.";
                    ShowNotification($"İndirme iptal edildi: {item.Title}");
                }
            }
            catch (Exception exception)
            {
                item.Status = "Başarısız";
                item.Details = exception.Message;
                ShowNotification($"İndirme başarısız: {item.Title} — {exception.Message}");
            }
            finally
            {
                item.IsProcessing = false;
                item.ResetCancellation();
                if (item.ResumeRequested && item.Status == "Duraklatıldı")
                {
                    item.ResumeRequested = false;
                    item.Status = "Sırada";
                    _queueSignal.Release();
                    if (!_queueRunnerStarted) StartQueueRunner();
                }
            }
        }

        private void OpenDownloadLocationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: DownloadQueueItem item } ||
                string.IsNullOrWhiteSpace(item.DownloadDirectory))
            {
                ShowNotification("Bu indirme için konum bilgisi bulunamadı.");
                return;
            }

            if (!Directory.Exists(item.DownloadDirectory))
            {
                ShowNotification($"İndirme klasörü bulunamadı: {item.DownloadDirectory}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = item.DownloadDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ShowNotification($"İndirme klasörü açılamadı: {exception.Message}");
            }
        }

        private void CancelQueueItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { Tag: DownloadQueueItem item } && item.CanCancel)
            {
                if (item.Status == "Sırada")
                {
                    item.Status = "İptal edildi";
                    item.Details = "Kuyruktan iptal edildi.";
                }
                item.Cancellation.Cancel();
                if (_queueRunnerStarted) _queueSignal.Release();
            }
        }

        private void ResumeQueueItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { Tag: DownloadQueueItem item })
            {
                if (item.Status == "Duraklatıldı")
                {
                    if (item.IsProcessing)
                    {
                        item.ResumeRequested = true;
                        item.Details = "İndirme duraklatılıyor, ardından devam edecek.";
                    }
                    else
                    {
                        item.ResetCancellation();
                        item.Status = "Sırada";
                        item.ResumeRequested = false;
                        _queueSignal.Release();
                        StartQueueRunner();
                    }
                }
            }
        }

        private void PauseQueueItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { Tag: DownloadQueueItem item })
            {
                if (item.Status == "İndiriliyor")
                {
                    item.Status = "Duraklatıldı";
                    item.ResumeRequested = false;
                    item.Details = "Duraklatılıyor...";
                    item.Cancellation.Cancel();
                }
            }
        }

        private void RemoveQueueItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { Tag: DownloadQueueItem item })
            {
                var result = System.Windows.MessageBox.Show(
                    $"'{item.Title}' kaldırmak istediğinize emin misiniz?",
                    "Öğeyi Kaldır",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    _queue.Remove(item);
                    _completedQueue.Remove(item);
                    SaveCompletedHistory();
                    UpdateQueueStatus();
                }
            }
        }

        private void LoadCompletedHistory()
        {
            if (!File.Exists(_completedHistoryFilePath)) return;

            try
            {
                var records = JsonSerializer.Deserialize<List<CompletedDownloadRecord>>(
                    File.ReadAllText(_completedHistoryFilePath));
                if (records is null) return;

                foreach (var record in records)
                {
                    var item = new DownloadQueueItem(
                        record.Url,
                        record.Title,
                        record.ThumbnailUrl,
                        new YtDlpDownloadOptions())
                    {
                        Status = "Tamamlandı",
                        Progress = 100,
                        Details = record.Details,
                        DownloadDirectory = record.DownloadDirectory ?? TryGetDirectoryFromDetails(record.Details)
                    };
                    _completedQueue.Add(item);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                ShowNotification($"İndirme geçmişi okunamadı: {exception.Message}");
            }
        }

        private void SaveCompletedHistory()
        {
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                var records = _completedQueue.Select(item => new CompletedDownloadRecord(
                    item.Url,
                    item.Title,
                    item.ThumbnailUrl,
                    item.Details,
                    item.DownloadDirectory)).ToList();
                var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                var temporaryPath = _completedHistoryFilePath + ".tmp";
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, _completedHistoryFilePath, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ShowNotification($"İndirme geçmişi kaydedilemedi: {exception.Message}");
            }
        }

        private YtDlpDownloadOptions BuildOptions()
        {
            var videoFormatId = (VideoFormatComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            var audioFormatId = (AudioFormatComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

            var formatSelector = string.IsNullOrWhiteSpace(videoFormatId)
                ? "bv*+ba/b"
                : string.IsNullOrWhiteSpace(audioFormatId)
                ? videoFormatId
                : $"{videoFormatId}+{audioFormatId}";

            return new YtDlpDownloadOptions
            {
                FormatSelector = formatSelector,
            };
        }

        private List<string> GetUrls()
        {
            return UrlTextBox.Text.Split(['\r', '\n', '\t', ' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(TryGetHttpUri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryGetHttpUri(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private void SetInputBusy(bool isBusy)
        {
            LoadFormatsButton.IsEnabled = !isBusy;
            UrlTextBox.IsEnabled = !isBusy;
        }

        private void ShowNotification(string message)
        {
            _notificationVersion++;
            _notificationTimer.Stop();
            NotificationBanner.BeginAnimation(UIElement.OpacityProperty, null);
            NotificationTransform.BeginAnimation(TranslateTransform.YProperty, null);
            NotificationText.Text = message;
            NotificationBanner.Visibility = Visibility.Visible;
            NotificationBanner.Opacity = 0;
            NotificationTransform.Y = -30;
            var duration = TimeSpan.FromMilliseconds(350);
            NotificationBanner.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            NotificationTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-30, 0, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            _notificationTimer.Start();
        }

        private void HideNotification()
        {
            _notificationTimer.Stop();
            var notificationVersion = _notificationVersion;
            var duration = TimeSpan.FromMilliseconds(250);
            var fadeOut = new DoubleAnimation(NotificationBanner.Opacity, 0, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                if (_notificationVersion != notificationVersion) return;
                NotificationBanner.Visibility = Visibility.Collapsed;
                NotificationBanner.BeginAnimation(UIElement.OpacityProperty, null);
                NotificationTransform.BeginAnimation(TranslateTransform.YProperty, null);
                NotificationBanner.Opacity = 0;
                NotificationTransform.Y = -30;
            };
            NotificationBanner.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            NotificationTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(NotificationTransform.Y, -15, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                });
        }

        private void UpdateQueueStatus()
        {
            var activeDownloadCount = _queue.Count(item => item.Status == "İndiriliyor");
            StatusText.Text = activeDownloadCount == 0
                ? "Kuyruk boş"
                : $"Etkin indirme: {activeDownloadCount} görev";
        }

        private int LoadConcurrency()
        {
            try
            {
                if (File.Exists(_concurrencyFilePath) && int.TryParse(File.ReadAllText(_concurrencyFilePath), out var value))
                    return Math.Clamp(value, 1, 4);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            return 2;
        }

        private string LoadDownloadDirectory()
        {
            var defaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var savedDirectory = File.ReadAllText(_settingsFilePath).Trim();
                    if (!string.IsNullOrWhiteSpace(savedDirectory) && Directory.Exists(savedDirectory)) return savedDirectory;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            return defaultDirectory;
        }

        private static string GetHostTitle(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

        private static string? TryGetDirectoryFromDetails(string details)
        {
            const string prefix = "Kaydedildi: ";
            return details.StartsWith(prefix, StringComparison.Ordinal)
                ? details[prefix.Length..]
                : null;
        }

        private sealed record CompletedDownloadRecord(
            string Url,
            string Title,
            string? ThumbnailUrl,
            string Details,
            string? DownloadDirectory = null);
    }
}
