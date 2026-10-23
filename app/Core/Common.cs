using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TurboDM.Core
{
    public static class Categories
    {
        public const string Video = "Video", Music = "Müzik", Documents = "Belgeler", Compressed = "Sıkıştırılmış", Programs = "Programlar", Other = "Diğer";

        static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);
        static Categories()
        {
            void Add(string cat, string exts) { foreach (var e in exts.Split(' ')) Map["." + e] = cat; }
            Add(Video, "mp4 mkv avi mov wmv flv webm m4v ts mpg mpeg 3gp m2ts");
            Add(Music, "mp3 m4a aac flac wav ogg opus wma");
            Add(Documents, "pdf doc docx xls xlsx ppt pptx txt epub odt rtf csv");
            Add(Compressed, "zip rar 7z tar gz bz2 xz iso tgz");
            Add(Programs, "exe msi apk dmg deb rpm appimage msix");
        }

        public static readonly string[] All = { Video, Music, Documents, Compressed, Programs, Other };

        public static string Of(string fileName, DownloadKind kind)
        {
            if (Map.TryGetValue(Path.GetExtension(fileName ?? ""), out var c)) return c;
            return kind == DownloadKind.Direct ? Other : Video;
        }
    }

    public class Settings
    {
        public string DownloadFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TurboDM");
        public bool UseCategoryFolders { get; set; } = true;
        public int Connections { get; set; } = 8;
        public int MaxConcurrent { get; set; } = 3;
        public int SpeedLimitKBps { get; set; } = 0;
        public int Port { get; set; } = 9614;
        public bool ShowAddDialog { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool NotifyOnComplete { get; set; } = true;
        public string? YtDlpPath { get; set; }
        public string? FfmpegPath { get; set; }
        public string YtDlpFormat { get; set; } = "bv*+ba/b";
        /// <summary>Otomatik seçilecek ses/altyazı dilleri (virgülle).</summary>
        public string PreferredLanguages { get; set; } = "tr,en";
        public bool DownloadSubtitles { get; set; } = true;
        public bool DownloadAllTracks { get; set; } = false;

        public static string AppDataDir
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TurboDM");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string FilePath => Path.Combine(AppDataDir, "settings.json");

        public static Settings Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
            catch { }
            return new Settings();
        }

        public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

        public string FolderFor(string category) => UseCategoryFolders ? Path.Combine(DownloadFolder, category) : DownloadFolder;
    }

    /// <summary>Tüm indirmeler için ortak hız sınırlayıcı (token bucket).</summary>
    public class SpeedLimiter
    {
        private readonly object _lock = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _tokens, _last;
        public long LimitBytesPerSec { get; set; }

        public async ValueTask WaitAsync(int n, CancellationToken ct)
        {
            while (true)
            {
                long limit = LimitBytesPerSec;
                if (limit <= 0) return;
                TimeSpan wait;
                lock (_lock)
                {
                    double cap = Math.Max(256 * 1024, limit / 4.0); // küçük patlama payı
                    double now = _sw.Elapsed.TotalSeconds;
                    _tokens = Math.Min(cap, _tokens + (now - _last) * limit);
                    _last = now;
                    if (_tokens >= n) { _tokens -= n; return; }
                    wait = TimeSpan.FromSeconds((n - _tokens) / limit);
                }
                if (wait < TimeSpan.FromMilliseconds(5)) wait = TimeSpan.FromMilliseconds(5);
                await Task.Delay(wait, ct);
            }
        }
    }

    public static class Http
    {
        public const string DefaultUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

        public static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 15,
            AutomaticDecompression = DecompressionMethods.None, // Range ile uyum için şart
            UseCookies = false,                                  // Cookie başlığını kendimiz yollarız
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        public static HttpRequestMessage Build(HttpMethod m, string url, DownloadItem it, long? from = null, long? to = null)
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(it.UserAgent) ? DefaultUA : it.UserAgent);
            r.Headers.TryAddWithoutValidation("Accept", "*/*");
            if (!string.IsNullOrEmpty(it.Referer)) r.Headers.TryAddWithoutValidation("Referer", it.Referer);
            if (!string.IsNullOrEmpty(it.Cookies)) r.Headers.TryAddWithoutValidation("Cookie", it.Cookies);
            if (from.HasValue) r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
            return r;
        }

        public static async Task<string> GetStringAsync(string url, DownloadItem it, CancellationToken ct)
        {
            using var req = Build(HttpMethod.Get, url, it);
            using var res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(ct);
        }

        public static async Task<byte[]> GetBytesAsync(string url, DownloadItem it, CancellationToken ct)
        {
            using var req = Build(HttpMethod.Get, url, it);
            using var res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsByteArrayAsync(ct);
        }
    }

    public static class Files
    {
        public static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "indirme";
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
            var sb = new StringBuilder();
            foreach (var ch in name) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
            var s = sb.ToString().Trim().TrimEnd('.');
            if (s.Length > 180)
            {
                var ext = Path.GetExtension(s);
                s = s.Substring(0, 180 - ext.Length) + ext;
            }
            return s.Length == 0 ? "indirme" : s;
        }

        public static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                var p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        public static string NameFromUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                var last = Uri.UnescapeDataString(u.AbsolutePath.Split('/').LastOrDefault(s => s.Length > 0) ?? "");
                return Sanitize(last.Length > 0 ? last : u.Host);
            }
            catch { return "indirme"; }
        }

        public static string? ExtFromMime(string? mime) => mime?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            "video/quicktime" => ".mov",
            "video/mp2t" => ".ts",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/webm" => ".weba",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            "application/x-rar-compressed" or "application/vnd.rar" => ".rar",
            "application/x-7z-compressed" => ".7z",
            "application/x-msdownload" or "application/vnd.microsoft.portable-executable" => ".exe",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => null
        };

        /// <summary>ffmpeg / yt-dlp gibi araçları sırayla: ayar → uygulama klasörü → AppData\bin → PATH içinde arar.</summary>
        public static string? FindTool(string? configured, string exeName)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, exeName),
                Path.Combine(AppContext.BaseDirectory, "bin", exeName),
                Path.Combine(Settings.AppDataDir, "bin", exeName),
            };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim('"'), exeName));
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
