using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TurboDM.Core
{
    /// <summary>Kuyruk, eşzamanlılık, kalıcılık (downloads.json) ve indirici seçimi.</summary>
    public class DownloadManager
    {
        public ObservableCollection<DownloadItem> Items { get; } = new();
        public Settings Settings { get; private set; }
        public EngineContext Ctx { get; private set; }

        public event Action<DownloadItem>? ItemCompleted;
        public event Action<DownloadItem>? ItemFailed;

        readonly Action<Action> _ui;
        readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
        readonly object _saveLock = new();
        DateTime _lastSave = DateTime.MinValue;

        static string DbPath => Path.Combine(Settings.AppDataDir, "downloads.json");

        static readonly string[] YtDlpHosts =
        {
            "youtube.com", "youtu.be", "instagram.com", "tiktok.com", "twitter.com", "x.com", "vimeo.com",
            "dailymotion.com", "twitch.tv", "facebook.com", "fb.watch", "reddit.com", "soundcloud.com",
            "bilibili.com", "kick.com", "rumble.com", "pinterest.com", "linkedin.com", "threads.net", "bsky.app"
        };

        public DownloadManager(Settings settings, Action<Action> uiInvoke)
        {
            Settings = settings;
            Ctx = new EngineContext { Settings = settings, Limiter = new SpeedLimiter() };
            _ui = uiInvoke;
            ApplySettings(settings);
        }

        public void ApplySettings(Settings s)
        {
            Settings = s;
            Ctx = new EngineContext { Settings = s, Limiter = Ctx.Limiter };
            Ctx.Limiter.LimitBytesPerSec = s.SpeedLimitKBps * 1024L;
            Schedule();
        }

        public static bool IsYtDlpSite(string url)
        {
            try
            {
                var host = new Uri(url).Host.ToLowerInvariant();
                return YtDlpHosts.Any(h => host == h || host.EndsWith("." + h));
            }
            catch { return false; }
        }

        public DownloadItem Create(DownloadRequest r)
        {
            var url = r.Url.Trim();
            var lower = url.ToLowerInvariant();
            DownloadKind kind = (r.Kind ?? "auto").ToLowerInvariant() switch
            {
                "direct" => DownloadKind.Direct,
                "hls" => DownloadKind.Hls,
                "ytdlp" => DownloadKind.YtDlp,
                _ => lower.Contains(".m3u8") ? DownloadKind.Hls
                   : lower.Contains(".mpd") || IsYtDlpSite(url) ? DownloadKind.YtDlp
                   : DownloadKind.Direct
            };

            var it = new DownloadItem
            {
                Url = url, Referer = r.Referer, UserAgent = r.UserAgent, Cookies = r.Cookies, Kind = kind, AudioOnly = r.AudioOnly,
                ExternalSubs = r.Subtitles?.Where(s => Uri.TryCreate(s.Url, UriKind.Absolute, out _)).GroupBy(s => s.Url).Select(g => g.First()).Take(30).ToList(),
            };

            if (!string.IsNullOrWhiteSpace(r.FileName)) { it.FileName = Files.Sanitize(Path.GetFileName(r.FileName)); it.NameLocked = true; }
            else if (kind == DownloadKind.Hls) it.FileName = Files.Sanitize(string.IsNullOrWhiteSpace(r.PageTitle) ? "video" : r.PageTitle) + ".mp4";
            else if (kind == DownloadKind.YtDlp) it.FileName = Files.Sanitize(string.IsNullOrWhiteSpace(r.PageTitle) ? new Uri(url).Host : r.PageTitle);
            else it.FileName = Files.NameFromUrl(url);

            if (!string.IsNullOrWhiteSpace(r.SaveDir)) { it.SaveDir = r.SaveDir; it.DirLocked = true; }
            else it.SaveDir = Settings.FolderFor(r.AudioOnly ? Categories.Music : it.Category);
            return it;
        }

        public void Add(DownloadItem it, bool startNow)
        {
            it.Status = startNow ? DownloadStatus.Queued : DownloadStatus.Paused;
            Items.Insert(0, it);
            Save(true);
            Schedule();
        }

        public bool IsRunning(DownloadItem it) => _running.ContainsKey(it.Id);

        public void Schedule()
        {
            int active = _running.Count;
            foreach (var it in Items.Reverse().Where(i => i.Status == DownloadStatus.Queued).ToList())
            {
                if (active >= Math.Max(1, Settings.MaxConcurrent)) break;
                StartItem(it);
                active++;
            }
        }

        void StartItem(DownloadItem it)
        {
            var cts = new CancellationTokenSource();
            if (!_running.TryAdd(it.Id, cts)) return;
            it.ErrorMessage = null;
            it.ExternalPercent = -1; it.ExternalSpeed = -1;
            it.MarkStarted();
            it.Status = DownloadStatus.Downloading;
            var ctx = Ctx;

            Task.Run(async () =>
            {
                DownloadStatus final; string? err = null;
                try
                {
                    switch (it.Kind)
                    {
                        case DownloadKind.Hls: await new HlsDownloader().RunAsync(it, ctx, cts.Token); break;
                        case DownloadKind.YtDlp: await new YtDlpDownloader().RunAsync(it, ctx, cts.Token); break;
                        default: await new DirectDownloader().RunAsync(it, ctx, cts.Token); break;
                    }
                    final = DownloadStatus.Completed;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { final = DownloadStatus.Paused; }
                catch (Exception ex)
                {
                    final = DownloadStatus.Error;
                    while (ex is AggregateException { InnerException: not null } ag) ex = ag.InnerException!;
                    err = ex.Message;
                }

                _ui(() =>
                {
                    _running.TryRemove(it.Id, out _);
                    cts.Dispose();
                    if (it.Status != DownloadStatus.Queued || final != DownloadStatus.Paused) // yeniden başlatma isteği bekliyorsa koru
                        it.Status = final;
                    it.ErrorMessage = err;
                    it.ExternalPercent = -1; it.ExternalSpeed = -1;
                    if (final == DownloadStatus.Completed) { it.CompletedAt = DateTime.Now; ItemCompleted?.Invoke(it); }
                    if (final == DownloadStatus.Error) ItemFailed?.Invoke(it);
                    it.Refresh();
                    Save(true);
                    Schedule();
                });
            });
        }

        public void Resume(DownloadItem it)
        {
            if (it.Status is DownloadStatus.Paused or DownloadStatus.Error)
            {
                it.Status = DownloadStatus.Queued;
                Schedule();
            }
        }

        public void Pause(DownloadItem it)
        {
            if (_running.TryGetValue(it.Id, out var cts)) cts.Cancel();
            else if (it.Status == DownloadStatus.Queued) it.Status = DownloadStatus.Paused;
        }

        public void PauseAll() { foreach (var it in Items.ToList()) Pause(it); }
        public void ResumeAll() { foreach (var it in Items.ToList()) Resume(it); }

        public void Restart(DownloadItem it)
        {
            Pause(it);
            // Parça listesi silinince indirici geçici dosyanın üzerine baştan yazar
            lock (it.SyncRoot) { it.Segments.Clear(); it.DownloadedBytes = 0; it.TotalBytes = 0; it.HlsDone = 0; }
            it.Status = DownloadStatus.Queued;
            Schedule();
        }

        public void Remove(DownloadItem it, bool deleteFiles)
        {
            Pause(it);
            Items.Remove(it);
            var del = TempPaths(it).ToList();
            if (deleteFiles && it.Status == DownloadStatus.Completed) del.Add(it.FullPath);
            DeleteLater(del, null);
            Save(true);
        }

        public void RemoveCompleted() { foreach (var it in Items.Where(i => i.Status == DownloadStatus.Completed).ToList()) Items.Remove(it); Save(true); }

        static IEnumerable<string> TempPaths(DownloadItem it)
        {
            yield return it.FullPath + ".tdm";
            yield return Path.Combine(it.SaveDir, ".tdm_" + it.Id);
        }

        static void DeleteLater(List<string> paths, Action? then)
        {
            Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    bool ok = true;
                    foreach (var p in paths)
                    {
                        try
                        {
                            if (Directory.Exists(p)) Directory.Delete(p, true);
                            else if (File.Exists(p)) File.Delete(p);
                        }
                        catch { ok = false; }
                    }
                    if (ok) break;
                    await Task.Delay(250);
                }
                then?.Invoke();
            });
        }

        /// <summary>UI zamanlayıcısı her 500ms'de çağırır.</summary>
        public double Tick()
        {
            double total = 0;
            foreach (var it in Items)
            {
                if (it.IsActive || it.Speed > 0) it.Refresh();
                if (it.Status == DownloadStatus.Downloading) total += it.Speed;
            }
            if (_running.Count > 0 && (DateTime.Now - _lastSave).TotalSeconds > 5) Save(false);
            return total;
        }

        public int ActiveCount => _running.Count;

        // ---------------- Kalıcılık ----------------

        public void Save(bool force)
        {
            _lastSave = DateTime.Now;
            var arr = new JsonArray();
            foreach (var it in Items.ToList())
                lock (it.SyncRoot) arr.Add(JsonSerializer.SerializeToNode(it));
            var json = arr.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            Task.Run(() =>
            {
                lock (_saveLock)
                {
                    try
                    {
                        var tmp = DbPath + ".tmp";
                        File.WriteAllText(tmp, json);
                        File.Move(tmp, DbPath, true);
                    }
                    catch { }
                }
            });
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(DbPath)) return;
                var list = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(DbPath)) ?? new();
                foreach (var it in list)
                {
                    if (it.Status is DownloadStatus.Downloading or DownloadStatus.Processing or DownloadStatus.Queued) it.Status = DownloadStatus.Paused;
                    it.Refresh();
                    Items.Add(it);
                }
            }
            catch { }
        }

        public void FlushSync()
        {
            PauseAll();
            var arr = new JsonArray();
            foreach (var it in Items.ToList()) lock (it.SyncRoot) arr.Add(JsonSerializer.SerializeToNode(it));
            lock (_saveLock) { try { File.WriteAllText(DbPath, arr.ToJsonString()); } catch { } }
        }
    }
}
