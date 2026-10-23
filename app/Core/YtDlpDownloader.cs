using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboDM.Core
{
    /// <summary>
    /// YouTube, Instagram, X, TikTok, Twitch, Vimeo vb. 1000+ site için yt-dlp'yi çalıştırır ve
    /// ilerlemesini okur. yt-dlp.exe yoksa GitHub'dan otomatik indirir.
    /// </summary>
    public class YtDlpDownloader
    {
        const string ReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        static readonly SemaphoreSlim InstallLock = new(1, 1);

        static string ExeName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

        public static async Task<string> EnsureAsync(Settings s, CancellationToken ct)
        {
            var found = Files.FindTool(s.YtDlpPath, ExeName);
            if (found != null) { await MaybeUpdate(found); return found; }

            await InstallLock.WaitAsync(ct);
            try
            {
                found = Files.FindTool(s.YtDlpPath, ExeName);
                if (found != null) return found;
                if (!OperatingSystem.IsWindows()) throw new FileNotFoundException("yt-dlp bulunamadı (PATH'e ekleyin)");
                var dir = Path.Combine(Settings.AppDataDir, "bin");
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, ExeName);
                var tmp = target + ".download";
                using (var res = await Http.Client.GetAsync(ReleaseUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    res.EnsureSuccessStatusCode();
                    await using var f = File.Create(tmp);
                    await res.Content.CopyToAsync(f, ct);
                }
                File.Move(tmp, target, true);
                return target;
            }
            finally { InstallLock.Release(); }
        }

        static int _updateStarted;
        /// <summary>yt-dlp sık güncellenir; 1 günden eskiyse arka planda "-U" çalıştırılır.</summary>
        static async Task MaybeUpdate(string exe)
        {
            try
            {
                if (Interlocked.Exchange(ref _updateStarted, 1) == 1) return;
                if ((DateTime.Now - File.GetLastWriteTime(exe)).TotalDays < 1) return;
                await HlsDownloader.RunProcess(exe, new[] { "-U" }, CancellationToken.None);
            }
            catch { }
        }

        static bool IsYouTube(string url)
        {
            try
            {
                var h = new Uri(url).Host.ToLowerInvariant();
                return h == "youtu.be" || h.EndsWith("youtube.com") || h.EndsWith("youtube-nocookie.com");
            }
            catch { return false; }
        }

        public async Task RunAsync(DownloadItem it, EngineContext ctx, CancellationToken ct)
        {
            var exe = await EnsureAsync(ctx.Settings, ct);
            var ffmpeg = Files.FindTool(ctx.Settings.FfmpegPath, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            bool yt = IsYouTube(it.Url);
            // YouTube artık imza çözümü için bir JavaScript çalıştırıcısı (Deno) istiyor; yoksa formatlar eksik gelir
            if (yt) await Tools.TryEnsureDenoAsync(ct);
            Directory.CreateDirectory(it.SaveDir);

            var started = DateTime.UtcNow;

            // ---- Yalnızca ses (dublaj) modu: seçilen her dil ayrı ses dosyası ----
            var selectedAudio = (it.AudioTracks ?? new List<string>()).Where(k => k.Length > 0).Distinct().ToList();
            if (it.AudioOnly)
            {
                var langs = selectedAudio.Count > 0 ? selectedAudio : new List<string> { "" };
                string? first = null;
                foreach (var l in langs)
                {
                    var attemptsA = new List<(string? f, string? sort)>();
                    if (l.Length > 0) attemptsA.Add(($"ba[language^={l}]", "ext:m4a"));
                    attemptsA.Add(("ba/b", "ext:m4a"));
                    var tmpl = (it.NameLocked ? Path.GetFileNameWithoutExtension(it.FileName) : "%(title).150B")
                               + (langs.Count > 1 && l.Length > 0 ? "." + l : "") + ".%(ext)s";
                    var path = await RunAttempts(exe, it, ctx, ffmpeg, yt, attemptsA, new List<string>(), false, tmpl, ct);
                    first ??= path;
                }
                Finish(it, first);
                return;
            }

            // ---- Video + seçilen ses (dublaj) ve altyazılar ----
            var audioLangs = selectedAudio;
            List<string> subLangs; bool autoSubs = false;
            if (it.SubTracks != null)
            {
                subLangs = it.SubTracks.Select(k => k.StartsWith("auto:") ? k[5..] : k).Distinct().ToList();
                autoSubs = it.SubTracks.Any(k => k.StartsWith("auto:"));
            }
            else if (ctx.Settings.DownloadAllTracks) subLangs = new() { "all", "-live_chat" };
            else if (ctx.Settings.DownloadSubtitles) subLangs = Langs.ParseList(ctx.Settings.PreferredLanguages).Select(l => Langs.Normalize(l) + "(-.*)?").ToList();
            else subLangs = new();
            bool multiAudio = audioLangs.Count > 1 && ffmpeg != null;
            bool embed = subLangs.Count > 0 && ffmpeg != null;
            var extra = new List<string>();
            if (subLangs.Count > 0)
            {
                extra.Add("--write-subs");
                if (autoSubs) extra.Add("--write-auto-subs");
                extra.AddRange(new[] { "--sub-langs", string.Join(",", subLangs) });
                // no-keep-subs: gömdükten sonra .srt/.vtt dosyalarını klasörde bırakma
                if (embed) extra.AddRange(new[] { "--convert-subs", "srt", "--embed-subs", "--compat-options", "no-keep-subs" });
            }
            if (multiAudio) extra.Add("--audio-multistreams");
            bool mkv = multiAudio || embed;

            // Format denemeleri: kullanıcının seçimi → geniş yedek → yt-dlp varsayılanı
            var attempts = new List<(string? f, string? sort)>();
            if (multiAudio)
                attempts.Add(("bv*+" + string.Join("+", audioLangs.Select(l => $"ba[language^={l}]")), "res,ext:mp4:m4a"));
            else if (audioLangs.Count == 1 && ffmpeg != null)
                attempts.Add(($"bv*+ba[language^={audioLangs[0]}]/{ctx.Settings.YtDlpFormat}", "res,ext:mp4:m4a"));
            attempts.Add((ffmpeg != null ? ctx.Settings.YtDlpFormat : "b", "res,ext:mp4:m4a"));
            attempts.Add((ffmpeg != null ? "bv*+ba/b/bv*/ba*" : "b/bv*/ba*", null));
            attempts.Add((null, null));

            var final = await RunAttempts(exe, it, ctx, ffmpeg, yt, attempts, extra, mkv, null, ct);
            if (embed && final != null) CleanupSidecars(final, started);
            Finish(it, final);
        }

        static void Finish(DownloadItem it, string? finalPath)
        {
            it.ExternalSpeed = -1; it.ExternalPercent = -1;
            if (finalPath != null && File.Exists(finalPath))
            {
                it.FileName = Path.GetFileName(finalPath);
                it.SaveDir = Path.GetDirectoryName(finalPath)!;
                it.TotalBytes = new FileInfo(finalPath).Length;
                it.DownloadedBytes = it.TotalBytes;
            }
        }

        /// <summary>Altyazılar videoya gömüldüyse yanında kalan "video.tr.srt" gibi dosyaları siler.</summary>
        static void CleanupSidecars(string videoPath, DateTime startedUtc)
        {
            try
            {
                var dir = Path.GetDirectoryName(videoPath)!;
                var stem = Path.GetFileNameWithoutExtension(videoPath) + ".";
                string[] exts = { ".srt", ".vtt", ".ass", ".ssa", ".ttml", ".srv1", ".srv2", ".srv3", ".json3" };
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!exts.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                    if (File.GetLastWriteTimeUtc(f) < startedUtc.AddMinutes(-1)) continue; // eski, kullanıcının dosyası olabilir
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>Format denemelerini sırayla çalıştırır; başarılı olursa son dosya yolunu döndürür.</summary>
        async Task<string?> RunAttempts(string exe, DownloadItem it, EngineContext ctx, string? ffmpeg, bool yt,
            List<(string? f, string? sort)> attempts, List<string> extra, bool mkv, string? outTemplate, CancellationToken ct)
        {
            string lastError = "";
            for (int i = 0; i < attempts.Count; i++)
            {
                var (code, err, finalPath) = await RunOnce(exe, BuildArgs(it, ctx, ffmpeg, yt, attempts[i].f, attempts[i].sort, extra, mkv, outTemplate), it, ct);
                if (code == 0) return finalPath;
                lastError = string.IsNullOrEmpty(err) ? $"yt-dlp hata kodu {code}" : err;
                bool formatProblem = lastError.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
                                  || lastError.Contains("No video formats found", StringComparison.OrdinalIgnoreCase);
                bool subProblem = extra.Contains("--write-subs") && lastError.Contains("subtitle", StringComparison.OrdinalIgnoreCase);
                if (subProblem)
                {
                    // altyazı indirilemedi → altyazısız tekrar dene
                    extra.RemoveAll(a => a is "--write-subs" or "--write-auto-subs" or "--embed-subs");
                    foreach (var opt in new[] { "--sub-langs", "--convert-subs", "--compat-options" })
                    { int x = extra.IndexOf(opt); if (x >= 0) extra.RemoveRange(x, 2); }
                    i--; continue;
                }
                if (!formatProblem) break;
                it.ExternalPercent = -1;
            }
            it.ExternalSpeed = -1;
            throw new Exception(Friendly(lastError));
        }

        /// <summary>Onay penceresi için: videonun dublaj (ses dili) ve altyazı seçeneklerini yt-dlp -J ile okur.</summary>
        public static async Task<List<MediaTrack>> AnalyzeAsync(DownloadItem it, Settings settings, CancellationToken ct)
        {
            var exe = await EnsureAsync(settings, ct);
            bool yt = IsYouTube(it.Url);
            if (yt) await Tools.TryEnsureDenoAsync(ct);
            var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8" };
            if (!yt)
            {
                if (!string.IsNullOrEmpty(it.Referer)) { args.Add("--referer"); args.Add(it.Referer); }
                if (!string.IsNullOrEmpty(it.Cookies)) { args.Add("--add-header"); args.Add("Cookie:" + it.Cookies); }
            }
            args.Add("--"); args.Add(it.Url);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PATH"] = Path.Combine(Settings.AppDataDir, "bin") + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            var json = await outTask;
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) throw new Exception(Friendly((await errTask).Replace("ERROR:", "").Trim()));

            var list = new List<MediaTrack>();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Ses dilleri: yalnızca sesten oluşan formatların "language" alanı
            if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var langs = new Dictionary<string, (string note, bool def)>();
                foreach (var f in formats.EnumerateArray())
                {
                    string Get(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                    if (Get("vcodec") != "none" || Get("acodec") is "" or "none") continue;
                    var lang = Get("language");
                    if (lang.Length == 0) continue;
                    var note = Get("format_note");
                    bool def = note.Contains("original", StringComparison.OrdinalIgnoreCase) || note.Contains("default", StringComparison.OrdinalIgnoreCase);
                    if (!langs.ContainsKey(lang) || def) langs[lang] = (note, def || (langs.TryGetValue(lang, out var o) && o.def));
                }
                if (langs.Count > 1)
                    foreach (var (lang, v) in langs.OrderByDescending(x => x.Value.def))
                        list.Add(new MediaTrack { Kind = TrackKind.Audio, Key = lang, Language = lang, Name = v.def ? "Orijinal" : "", IsDefault = v.def });
            }

            void Subs(string prop, bool auto)
            {
                if (!root.TryGetProperty(prop, out var subs) || subs.ValueKind != System.Text.Json.JsonValueKind.Object) return;
                var pref = Langs.ParseList(settings.PreferredLanguages);
                foreach (var s in subs.EnumerateObject())
                {
                    if (s.Name == "live_chat") continue;
                    // otomatik altyazılarda yüzlerce çeviri dili olur: yalnızca tercih edilenleri ve orijinali göster
                    if (auto && !Langs.Matches(s.Name, pref) && !s.Name.EndsWith("-orig")) continue;
                    if (auto && list.Any(t => t.Kind == TrackKind.Subtitle && !t.IsAuto && t.Language == s.Name)) continue;
                    string name = "";
                    if (s.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var e in s.Value.EnumerateArray())
                            if (e.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String) { name = n.GetString() ?? ""; break; }
                    list.Add(new MediaTrack { Kind = TrackKind.Subtitle, Key = auto ? "auto:" + s.Name : s.Name, Language = s.Name, Name = name, IsAuto = auto });
                }
            }
            Subs("subtitles", false);
            Subs("automatic_captions", true);
            return list;
        }

        static string Friendly(string err)
        {
            if (err.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
                return "YouTube uygun format vermedi. Ayarlar > 'yt-dlp'yi indir / güncelle' butonuna basıp tekrar deneyin. (Ayrıntı: %AppData%\\TurboDM\\logs\\yt-dlp.log)";
            if (err.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
                return "YouTube bot doğrulaması istedi; bir süre sonra tekrar deneyin.";
            if (err.Contains("Private video", StringComparison.OrdinalIgnoreCase)) return "Bu video gizli.";
            return Shorten(err);
        }

        static List<string> BuildArgs(DownloadItem it, EngineContext ctx, string? ffmpeg, bool yt, string? format, string? sort, List<string> extra, bool mkv, string? outTemplate = null)
        {
            var args = new List<string>
            {
                "--newline", "--progress", "--no-simulate", "--no-playlist", "--no-mtime", "--encoding", "utf-8",
                "--concurrent-fragments", Math.Clamp(ctx.Settings.Connections, 1, 16).ToString(),
                "-P", it.SaveDir,
                "-o", outTemplate ?? (it.NameLocked ? Path.GetFileNameWithoutExtension(it.FileName) + ".%(ext)s" : "%(title).150B.%(ext)s"),
                "--progress-template", "download:[TDM]%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress._percent_str)s",
                "--print", "before_dl:[TDMNAME]%(filename)s",
                "--print", "after_move:[TDMFILE]%(filepath)s",
            };
            if (format != null) { args.Add("-f"); args.Add(format); }
            if (sort != null) { args.Add("-S"); args.Add(sort); }
            if (ffmpeg != null)
            {
                args.AddRange(new[] { "--ffmpeg-location", ffmpeg });
                if (it.AudioOnly) args.AddRange(new[] { "-x", "--audio-format", "mp3", "--audio-quality", "0" });
                else args.AddRange(new[] { "--merge-output-format", mkv ? "mkv" : "mp4/mkv" });
            }
            args.AddRange(extra);
            if (ctx.Settings.SpeedLimitKBps > 0) { args.Add("-r"); args.Add(ctx.Settings.SpeedLimitKBps + "K"); }
            // YouTube'da tarayıcının çerezi / User-Agent'ı yt-dlp'nin kendi istemcilerini bozar ve
            // "Requested format is not available" hatasına yol açar; bu yüzden YouTube'a gönderilmez.
            if (!yt)
            {
                if (!string.IsNullOrEmpty(it.Referer)) { args.Add("--referer"); args.Add(it.Referer); }
                if (!string.IsNullOrEmpty(it.Cookies))
                {
                    args.Add("--add-header"); args.Add("Cookie:" + it.Cookies);
                    if (!string.IsNullOrEmpty(it.UserAgent)) { args.Add("--user-agent"); args.Add(it.UserAgent); }
                }
            }
            args.Add("--");
            args.Add(it.Url);
            return args;
        }

        async Task<(int code, string err, string? finalPath)> RunOnce(string exe, List<string> args, DownloadItem it, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
            // AppData\TurboDM\bin (deno, ffmpeg) PATH'e eklensin ki yt-dlp bulsun
            var bin = Path.Combine(Settings.AppDataDir, "bin");
            psi.Environment["PATH"] = bin + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
            foreach (var a in args) psi.ArgumentList.Add(a);

            var logDir = Path.Combine(Settings.AppDataDir, "logs");
            Directory.CreateDirectory(logDir);
            var log = new StringBuilder();
            log.AppendLine("> yt-dlp " + string.Join(" ", args.Select(a => a.StartsWith("Cookie:") ? "Cookie:<gizli>" : a.Contains(' ') ? $"\"{a}\"" : a)));

            using var p = new Process { StartInfo = psi };
            string lastError = "";
            string? finalPath = null;
            int phase = 0; double lastPct = 0;

            p.OutputDataReceived += (_, e) =>
            {
                var line = e.Data;
                if (string.IsNullOrEmpty(line)) return;
                if (line.StartsWith("[TDM]"))
                {
                    var f = line.Substring(5).Split('|');
                    long dl = ParseL(f.ElementAtOrDefault(0)), tot = ParseL(f.ElementAtOrDefault(1)), est = ParseL(f.ElementAtOrDefault(2));
                    double sp = ParseD(f.ElementAtOrDefault(3));
                    double pct = ParseD(f.ElementAtOrDefault(4)?.Trim().TrimEnd('%'));
                    if (pct + 20 < lastPct) phase++; // video bitti, ses başladı
                    lastPct = pct;
                    if (dl >= 0) it.DownloadedBytes = dl;
                    if (tot > 0) it.TotalBytes = tot; else if (est > 0) it.TotalBytes = est;
                    it.ExternalSpeed = sp >= 0 ? sp : 0;
                    if (pct >= 0) it.ExternalPercent = phase == 0 ? pct : Math.Min(99.9, pct);
                    return;
                }
                lock (log) log.AppendLine(line);
                if (line.StartsWith("[TDMNAME]")) { var n = Path.GetFileName(line.Substring(9)); if (n.Length > 0) it.FileName = n; }
                else if (line.StartsWith("[TDMFILE]")) finalPath = line.Substring(9);
                else if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.StartsWith("[FixupM3u8]") || line.StartsWith("[VideoConvertor]"))
                    it.Status = DownloadStatus.Processing;
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (log) log.AppendLine(e.Data);
                if (e.Data.Contains("ERROR")) lastError = e.Data.Replace("ERROR:", "").Trim();
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using (ct.Register(() => { try { p.Kill(true); } catch { } }))
                await p.WaitForExitAsync();
            p.WaitForExit(); // çıktı tamponlarının boşalması için
            try { lock (log) File.WriteAllText(Path.Combine(logDir, "yt-dlp.log"), log.ToString()); } catch { }
            ct.ThrowIfCancellationRequested();
            return (p.ExitCode, lastError, finalPath);
        }

        static string Shorten(string s) => s.Length > 200 ? s.Substring(0, 200) + "…" : s;
        static long ParseL(string? s) => long.TryParse(s?.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
        static double ParseD(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
    }
}
