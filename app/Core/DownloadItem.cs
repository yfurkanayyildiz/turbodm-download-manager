using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;

namespace TurboDM.Core
{
    public enum DownloadKind { Direct, Hls, YtDlp }

    public enum DownloadStatus { Queued, Downloading, Paused, Completed, Error, Processing }

    public class Segment
    {
        public long Start { get; set; }
        public long End { get; set; }          // inclusive
        public long Downloaded;                 // bytes written from Start
        [JsonIgnore] public bool Active;

        [JsonPropertyName("Downloaded")]
        public long DownloadedJson { get => Interlocked.Read(ref Downloaded); set => Downloaded = value; }

        [JsonIgnore] public long Remaining => End - Start + 1 - Interlocked.Read(ref Downloaded);
    }

    /// <summary>Eklentiden veya "URL ekle" penceresinden gelen istek.</summary>
    public class DownloadRequest
    {
        public string Url { get; set; } = "";
        public string? FileName { get; set; }
        public string? Referer { get; set; }
        public string? UserAgent { get; set; }
        public string? Cookies { get; set; }
        public string? Kind { get; set; }      // auto | direct | hls | ytdlp
        public string? PageTitle { get; set; }
        public string? SaveDir { get; set; }
        public bool Silent { get; set; }
        public bool AudioOnly { get; set; }
        /// <summary>Sayfada yakalanan harici altyazı dosyaları (.vtt/.srt) — oynatıcı ayrı yüklüyorsa.</summary>
        public List<ExternalSub>? Subtitles { get; set; }
    }

    public class ExternalSub
    {
        public string Url { get; set; } = "";
        public string? Lang { get; set; }
        public string? Name { get; set; }
        /// <summary>Altyazıyı yükleyen oynatıcı çerçevesinin adresi/kaynağı (çoğu altyazı sunucusu bunu kontrol eder).</summary>
        public string? Referer { get; set; }
        public string? Origin { get; set; }
        public string? Cookies { get; set; }
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Url { get; set; } = "";
        public string? Referer { get; set; }
        public string? UserAgent { get; set; }
        public string? Cookies { get; set; }
        public DownloadKind Kind { get; set; }
        public string SaveDir { get; set; } = "";
        public bool Resumable { get; set; }
        public bool NameLocked { get; set; }
        public bool DirLocked { get; set; }
        public bool AudioOnly { get; set; }
        /// <summary>Seçilen ses / altyazı izlerinin anahtarları. null = ayarlardaki varsayılanlar.</summary>
        public List<string>? AudioTracks { get; set; }
        public List<string>? SubTracks { get; set; }
        public List<ExternalSub>? ExternalSubs { get; set; }
        [JsonIgnore] public readonly object SyncRoot = new();
        public DateTime AddedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
        public List<Segment> Segments { get; set; } = new();
        public int HlsDone { get; set; }
        public int HlsTotal { get; set; }

        private string _fileName = "";
        public string FileName { get => _fileName; set { _fileName = value; OnChanged(); OnChanged(nameof(Category)); } }

        private DownloadStatus _status = DownloadStatus.Queued;
        public DownloadStatus Status { get => _status; set { _status = value; OnChanged(); OnChanged(nameof(StatusText)); OnChanged(nameof(IsActive)); } }

        private string? _error;
        public string? ErrorMessage { get => _error; set { _error = value; OnChanged(); OnChanged(nameof(StatusText)); } }

        // Thread-safe sayaçlar (indirici iş parçacıkları yazar, UI zamanlayıcısı okur)
        public long TotalBytes;
        public long DownloadedBytes;
        [JsonPropertyName("TotalBytes")] public long TotalBytesJson { get => Interlocked.Read(ref TotalBytes); set => TotalBytes = value; }
        [JsonPropertyName("DownloadedBytes")] public long DownloadedBytesJson { get => Interlocked.Read(ref DownloadedBytes); set => DownloadedBytes = value; }

        /// <summary>yt-dlp gibi yüzdeyi doğrudan bildiren indiriciler için.</summary>
        [JsonIgnore] public double ExternalPercent = -1;
        [JsonIgnore] public double ExternalSpeed = -1;

        [JsonIgnore] public string FullPath => System.IO.Path.Combine(SaveDir, FileName);
        [JsonIgnore] public string Category => Categories.Of(FileName, Kind);
        [JsonIgnore] public bool IsActive => Status == DownloadStatus.Downloading || Status == DownloadStatus.Processing;

        // ---- UI için hesaplanan alanlar (Refresh ile güncellenir) ----
        [JsonIgnore] public double Speed { get; private set; }
        [JsonIgnore] public double Progress { get; private set; }
        [JsonIgnore] public string ProgressText => Status == DownloadStatus.Completed ? "100%" : Progress <= 0 && Interlocked.Read(ref TotalBytes) <= 0 && HlsTotal == 0 ? "—" : $"{Progress:0.0}%";
        [JsonIgnore] public string SizeText => Interlocked.Read(ref TotalBytes) > 0 ? Fmt.Bytes(Interlocked.Read(ref TotalBytes)) : (Interlocked.Read(ref DownloadedBytes) > 0 ? Fmt.Bytes(Interlocked.Read(ref DownloadedBytes)) + "+" : "Bilinmiyor");
        [JsonIgnore] public string SpeedText => Status == DownloadStatus.Downloading && Speed > 0 ? Fmt.Bytes((long)Speed) + "/s" : "";
        [JsonIgnore] public string EtaText { get; private set; } = "";
        [JsonIgnore]
        public string StatusText => Status switch
        {
            DownloadStatus.Queued => "Sırada",
            DownloadStatus.Downloading => Kind == DownloadKind.Hls ? $"İndiriliyor ({HlsDone}/{HlsTotal} parça)" : "İndiriliyor",
            DownloadStatus.Paused => "Duraklatıldı",
            DownloadStatus.Completed => "Tamamlandı",
            DownloadStatus.Error => "Hata: " + ErrorMessage,
            DownloadStatus.Processing => "Birleştiriliyor…",
            _ => ""
        };

        private long _lastBytes = -1;
        private DateTime _lastTick;
        private double _smoothed;

        /// <summary>UI iş parçacığından ~500ms'de bir çağrılır.</summary>
        public void Refresh()
        {
            var now = DateTime.UtcNow;
            long done = Interlocked.Read(ref DownloadedBytes);
            long total = Interlocked.Read(ref TotalBytes);
            if (Status == DownloadStatus.Downloading)
            {
                if (ExternalSpeed >= 0) _smoothed = ExternalSpeed;
                else if (_lastBytes >= 0)
                {
                    double dt = (now - _lastTick).TotalSeconds;
                    if (dt > 0)
                    {
                        double inst = Math.Max(0, (done - _lastBytes) / dt);
                        _smoothed = _smoothed <= 0 ? inst : _smoothed * 0.7 + inst * 0.3;
                    }
                }
            }
            else _smoothed = 0;
            _lastBytes = done; _lastTick = now;
            Speed = _smoothed;

            if (Status == DownloadStatus.Completed) Progress = 100;
            else if (ExternalPercent >= 0) Progress = ExternalPercent;
            else if (Kind == DownloadKind.Hls && HlsTotal > 0) Progress = 100.0 * HlsDone / HlsTotal;
            else if (total > 0) Progress = Math.Min(100, 100.0 * done / total);
            else Progress = 0;

            EtaText = "";
            if (Status == DownloadStatus.Downloading && Speed > 1)
            {
                double remainingSec = -1;
                if (total > 0 && ExternalPercent < 0 && Kind != DownloadKind.Hls) remainingSec = (total - done) / Speed;
                else if (Progress > 0.5) remainingSec = (now - _startedAt).TotalSeconds * (100 - Progress) / Progress;
                if (remainingSec >= 0) EtaText = Fmt.Duration(remainingSec);
            }

            OnChanged(nameof(Speed)); OnChanged(nameof(SpeedText)); OnChanged(nameof(Progress));
            OnChanged(nameof(ProgressText)); OnChanged(nameof(SizeText)); OnChanged(nameof(EtaText)); OnChanged(nameof(StatusText));
        }

        private DateTime _startedAt = DateTime.UtcNow;
        public void MarkStarted() { _startedAt = DateTime.UtcNow; _lastBytes = -1; _smoothed = 0; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class Fmt
    {
        public static string Bytes(long b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{v:0} {u[i]}" : $"{v:0.##} {u[i]}";
        }

        public static string Duration(double sec)
        {
            if (double.IsInfinity(sec) || double.IsNaN(sec)) return "";
            var t = TimeSpan.FromSeconds(Math.Min(sec, 359999));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}sa {t.Minutes}dk" : t.TotalMinutes >= 1 ? $"{t.Minutes}dk {t.Seconds}sn" : $"{t.Seconds}sn";
        }
    }
}
