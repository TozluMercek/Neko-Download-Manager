# Neko Downloader

Windows için geliştirilen, bağlantılardaki videoları yt-dlp üzerinden indirip indirme kuyruğundan yönetmeye yarayan masaüstü uygulaması.

> **GitHub kısa açıklama önerisi:** Windows için yt-dlp tabanlı video indirme uygulaması. Format seçimi, eşzamanlı indirme kuyruğu ve indirme geçmişi sunar.

## Özellikler

- Bir veya birden fazla HTTP/HTTPS bağlantısını indirme kuyruğuna ekleme
- İndirme öncesinde medya başlığı, küçük resmi ve mevcut video/ses biçimlerini görüntüleme
- Video ve ses biçimlerini ayrı ayrı seçme; ayrı akışlar gerektiğinde FFmpeg ile birleştirme
- İndirme ilerlemesini ve durumunu takip etme
- İndirmeleri iptal etme, duraklatma ve devam ettirme
- Aynı anda 1–4 indirme çalıştırma (varsayılan: 2)
- Tamamlanan indirmeleri geçmişte görüntüleme ve indirme klasörünü açma
- İndirme klasörünü seçme ve tercihi sonraki çalıştırmalar için saklama
- Uygulama içinden yt-dlp güncellemesini denetleme

## Kullanılan teknolojiler

- **C# ve .NET 10** — uygulama ve indirme iş mantığı
- **WPF** — Windows masaüstü arayüzü
- **yt-dlp** — medya bilgilerini alma, biçimleri listeleme ve indirme. Uygulama ilk çalıştırmada `yt-dlp.exe` dosyasını GitHub'daki resmi yt-dlp yayınlarından indirir.
- **FFmpeg** — ayrı video ve ses akışlarını birleştirmek için isteğe bağlı harici araç. Bu kullanım için uygulamaya FFmpeg konumu tanıtılmalıdır.

Proje dosyasında ayrıca NuGet paketi tanımlı değildir; kullanılan .NET API'leri platformun kendi bileşenleridir.

## Gereksinimler

- Windows
- .NET 10 SDK (geliştirme ve derleme için)
- Birleştirme gerektiren video/ses biçimleri için FFmpeg
- Desteklenen kaynak bağlantısı ve internet erişimi

## Derleme ve çalıştırma

Depoyu klonladıktan sonra çözüm klasöründe PowerShell veya terminal açın:

```powershell
dotnet restore .\Downloader.slnx
dotnet build .\Downloader.slnx
dotnet run --project .\Downloader\Downloader.csproj
```

Visual Studio'da `Downloader.slnx` çözümünü açıp `Downloader` projesini başlatarak da çalıştırabilirsiniz.

## Kullanım

1. İndirmeler ekranındaki alana bir veya daha fazla medya bağlantısı girin.
2. **Önizle** seçeneğiyle medya bilgilerini ve kullanılabilir biçimleri yükleyin.
3. Video ve ses biçimlerini seçip **Kuyruğa ekle** düğmesine basın.
4. Gerekirse Ayarlar ekranından indirme klasörünü ve eşzamanlı indirme sayısını değiştirin.
5. İndirmelerin durumunu kuyruktan, tamamlananları **Tamamlandı** ekranından takip edin.

## FFmpeg kurulumu

Ayrı video ve ses akışlarını birleştirmek için FFmpeg gereklidir. FFmpeg'i `PATH` ortam değişkeninde erişilebilir hale getirebilir veya uygulamanın FFmpeg uyarısında `ffmpeg.exe` dosyasını seçebilirsiniz. Uygulama seçilen yolu yerel kullanıcı ayarlarında saklar.

## Ayar ve geçmiş dosyaları

Uygulama ayarları ve indirme geçmişi Windows kullanıcı profilindeki `%LOCALAPPDATA%\Downloader` klasöründe saklanır. yt-dlp yürütülebilir dosyası da bu klasöre indirilir.

