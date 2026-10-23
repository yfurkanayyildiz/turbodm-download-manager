# TurboDM — IDM benzeri indirme yöneticisi

İki parçadan oluşur:

| Parça | Görevi |
|---|---|
| `app/` — C# / .NET 8 WPF masaüstü uygulaması | Çok bağlantılı hızlı indirme, kuyruk, geçmiş, HLS ve yt-dlp motoru |
| `extension/` — Chrome / Edge eklentisi (Manifest V3) | Sayfalardaki video/ses akışlarını yakalar, “İndir” butonu gösterir, tarayıcı indirmelerini uygulamaya devreder |

## Özellikler

- **Çok parçalı indirme:** Dosya 1–32 bağlantıya bölünür. Biten bağlantı en büyük kalan parçayı ikiye bölüp onu devralır (IDM'deki dinamik bölme). Bağlantı koparsa yeniden dener.
- **Duraklat / devam et:** İlerleme parça parça kaydedilir. Uygulama kapanıp açılsa da indirme kaldığı yerden devam eder.
- **Video yakalama:** Videonun üzerine gelince “TurboDM ile indir” butonu çıkar. Eklenti MP4/WebM, HLS (m3u8) ve DASH (mpd) akışlarını ağ trafiğinden yakalar. Master playlist'ten en yüksek kaliteyi seçer, ayrı ses izini de indirip birleştirir. AES-128 şifreli HLS'yi çözer.
- **Dublaj ve altyazı → tek MKV:** HLS yayınlarındaki tüm ses dilleri (dublajlar) ve altyazılar ile YouTube'daki çoklu ses ve altyazılar listelenir. Onay penceresinde istediğin dilleri işaretlersin; seçilenlerin hepsi dil etiketleriyle birlikte tek bir MKV dosyasında birleştirilir. Ayarlar'daki "Tercih edilen diller" (varsayılan `tr,en`) otomatik işaretlenir; onay penceresi kapalıyken de bu tercih kullanılır. Altyazılar videonun içine gömülür, klasörde ayrı .srt dosyası kalmaz. Oynatıcının altyazıyı ayrı .srt/.vtt dosyası olarak yüklediği sitelerde eklenti bu dosyaları da yakalar ve videoyla birlikte MKV'ye ekler (Türkçe karakterli Windows-1254 .srt'ler otomatik düzeltilir). Onay penceresindeki **"Yalnızca dublaj"** seçeneğiyle görüntü indirilmeden yalnızca seçilen dublajlar ses dosyası olarak kaydedilir (`ad.tr.m4a`, `ad.en.m4a`; YouTube'da .mp3).
- **yt-dlp entegrasyonu:** YouTube, Instagram, TikTok, X, Twitch, Vimeo, Facebook, Reddit ve 1000'den fazla sitede “En yüksek kalite” ya da “Yalnızca ses (MP3)” seçilebilir. yt-dlp.exe yoksa ilk kullanımda kendisi indirilir; haftada bir kendini günceller.
- **Tarayıcı indirmelerini yakalama:** Ayarlanan uzantılardaki (zip, exe, iso, mp4…) ya da belirli boyutun üstündeki indirmeler iptal edilip TurboDM'e aktarılır. Çerezler ve Referer da gönderildiği için giriş gerektiren indirmeler de çalışır. Uygulama kapalıysa indirme normal şekilde tarayıcıda yapılır.
- **Kuyruk ve geçmiş:** Aynı anda kaç indirme yapılacağı seçilebilir. Hız sınırı konabilir. Dosyalar Video, Müzik, Belgeler, Sıkıştırılmış ve Programlar klasörlerine ayrılır. Ekranda kategori filtresi, arama ve sağ tık menüsü vardır.
- **Sistem tepsisi:** Pencere kapatılınca arka planda çalışmaya devam eder, indirme bitince bildirim gösterir. İsteğe bağlı olarak Windows açılışında başlar.

> **Sınır:** Netflix, Disney+, Amazon Prime, Spotify gibi DRM korumalı (Widevine/FairPlay) içerikler indirilemez. Eklenti bunları “DRM — indirilemez” diye işaretler.

## Kurulum

### 1) Masaüstü uygulaması
Gereksinim: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bat
cd app
build.bat
```
Çıktı: `app\publish\TurboDM.exe` (tek dosya). Çalıştırın. Sağ alttaki yeşil nokta “Tarayıcı köprüsü açık (port 9614)” yazısını göstermeli.

Visual Studio ile de açabilirsiniz: `app\TurboDM.csproj`, sonra F5.

**ffmpeg (önerilir):** YouTube'da 1080p ve üzeri kalite ile HLS→MP4 dönüşümü için gerekir. Uygulamadaki sarı uyarı çubuğunda **“ffmpeg'i otomatik indir”** butonuna basmanız yeterli.

### 2) Chrome / Edge eklentisi
1. `chrome://extensions` (Edge'de `edge://extensions`) sayfasını açın.
2. Sağ üstten **Geliştirici modu**nu açın.
3. **Paketlenmemiş öğe yükle** butonuna basıp `extension` klasörünü seçin.
4. Eklentiyi araç çubuğuna sabitleyin. Simgedeki sayı, sayfada yakalanan medya sayısını gösterir.

## Kullanım
- **Video sitesi:** Videonun üzerine gelin, **TurboDM ile indir** butonuna basın, açılan menüden kaliteyi seçin.
- **Herhangi bir dosya:** Normal şekilde indirin. Eklenti indirmeyi TurboDM'e aktarır.
- **Sağ tık:** Bağlantı ya da video üzerinde **TurboDM ile indir** seçeneğini kullanın.
- **Elle:** Uygulamada **URL Ekle** (Ctrl+N) butonuna basın. Panodaki bağlantı otomatik doldurulur.
- Listede **Boşluk** tuşu duraklatır veya devam ettirir. **Del** listeden kaldırır, **Shift+Del** dosyayla birlikte siler. Çift tıklama dosyayı açar.

## Mimari
```
Chrome eklentisi ──HTTP POST /add──▶ TurboDM.exe (localhost:9614)
  • webRequest ile medya yakalama        • DirectDownloader (Range, çok bağlantı)
  • downloads.onCreated ile yakalama     • HlsDownloader (m3u8, AES-128, ffmpeg mux)
  • content script: video butonu         • YtDlpDownloader (yt-dlp süreci)
                                         • DownloadManager (kuyruk, downloads.json)
```
- Köprü yalnızca `localhost` üzerinde dinler. Yalnızca `chrome-extension://` kaynaklı istekleri kabul eder, böylece web siteleri uygulamaya indirme gönderemez.
- Veriler `%AppData%\TurboDM\` klasöründe tutulur (`settings.json`, `downloads.json`, `bin\yt-dlp.exe`, `bin\ffmpeg.exe`).
