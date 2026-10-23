using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TurboDM.Core
{
    /// <summary>
    /// HLS (m3u8) yayın indirici. Master playlist'ten en yüksek kaliteyi seçer, ayrı ses izini de alır,
    /// parçaları paralel indirir, AES-128 şifrelemeyi çözer ve ffmpeg varsa MP4'e birleştirir.
    /// DRM (Widevine/FairPlay/SAMPLE-AES) korumalı yayınlar desteklenmez.
    /// </summary>
    public class HlsDownloader
    {
        class Seg
        {
            public string Uri = "";
            public string? KeyUri;
            public byte[]? Iv;
            public long Sequence;
            public long? RangeLen, RangeOff;
        }

        class MediaPlaylist
        {
            public List<Seg> Segments = new();
            public string? InitUri;
            public long? InitLen, InitOff;
        }

        static readonly Regex AttrRx = new(@"([A-Z0-9-]+)=(""[^""]*""|[^,]*)", RegexOptions.Compiled);

        static Dictionary<string, string> Attrs(string line)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = line.IndexOf(':');
            if (i < 0) return d;
            foreach (Match m in AttrRx.Matches(line.Substring(i + 1)))
                d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
            return d;
        }

        static string Resolve(string baseUrl, string rel) => new Uri(new Uri(baseUrl), rel).ToString();

        // ---------------- Master playlist analizi ----------------

        class MasterInfo
        {
            public string VideoUrl = "";
            public List<MediaTrack> Audio = new();
            public List<MediaTrack> Subs = new();
        }

        static MasterInfo ParseMaster(string baseUrl, string master)
        {
            var lines = master.Split('\n').Select(l => l.Trim()).ToArray();
            var variants = new List<(long bw, long pixels, string uri, string? audioGroup, string? subGroup)>();
            var media = new List<(string type, string group, Dictionary<string, string> a)>();
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.StartsWith("#EXT-X-STREAM-INF"))
                {
                    var a = Attrs(l);
                    long bw = a.TryGetValue("BANDWIDTH", out var b) && long.TryParse(b, out var bv) ? bv : 0;
                    long px = 0;
                    if (a.TryGetValue("RESOLUTION", out var r) && r.Split('x') is { Length: 2 } wh && long.TryParse(wh[0], out var w) && long.TryParse(wh[1], out var h)) px = w * h;
                    a.TryGetValue("AUDIO", out var ag);
                    a.TryGetValue("SUBTITLES", out var sg);
                    int k = i + 1;
                    while (k < lines.Length && (lines[k].Length == 0 || lines[k].StartsWith("#"))) k++;
                    if (k < lines.Length) variants.Add((bw, px, Resolve(baseUrl, lines[k]), ag, sg));
                }
                else if (l.StartsWith("#EXT-X-MEDIA:"))
                {
                    var a = Attrs(l);
                    media.Add((a.GetValueOrDefault("TYPE", ""), a.GetValueOrDefault("GROUP-ID", ""), a));
                }
            }
            if (variants.Count == 0) throw new InvalidDataException("Master playlist'te kalite bulunamadı");
            var best = variants.OrderByDescending(v => v.pixels).ThenByDescending(v => v.bw).First();
            var info = new MasterInfo { VideoUrl = best.uri };

            MediaTrack Make(TrackKind kind, Dictionary<string, string> a)
            {
                var lang = a.GetValueOrDefault("LANGUAGE", "");
                var name = a.GetValueOrDefault("NAME", lang);
                if (lang.Length == 0) lang = Langs.Guess(name);   // LANGUAGE yoksa "Türkçe" gibi addan tahmin
                return new MediaTrack
                {
                    Kind = kind, Language = lang, Name = name, Key = $"{lang}|{name}",
                    IsDefault = a.GetValueOrDefault("DEFAULT", "") == "YES",
                    Uri = a.TryGetValue("URI", out var u) && u.Length > 0 ? Resolve(baseUrl, u) : null,
                };
            }

            if (best.audioGroup != null)
                foreach (var m in media.Where(m => m.type == "AUDIO" && m.group == best.audioGroup))
                    if (!info.Audio.Any(t => t.Key == Make(TrackKind.Audio, m.a).Key)) info.Audio.Add(Make(TrackKind.Audio, m.a));

            var subMedia = media.Where(m => m.type == "SUBTITLES" && (best.subGroup == null || m.group == best.subGroup));
            foreach (var m in subMedia)
            {
                var t = Make(TrackKind.Subtitle, m.a);
                if (t.Uri != null && !info.Subs.Any(x => x.Key == t.Key)) info.Subs.Add(t);
            }
            return info;
        }

        /// <summary>Onay penceresi için: yayındaki ses ve altyazı izlerini listeler.</summary>
        public static async Task<List<MediaTrack>> AnalyzeAsync(DownloadItem it, CancellationToken ct)
        {
            var text = await Http.GetStringAsync(it.Url, it, ct);
            if (!text.Contains("#EXT-X-STREAM-INF")) return new();
            var m = ParseMaster(it.Url, text);
            return m.Audio.Concat(m.Subs).ToList();
        }

        // ---------------- İndirme ----------------

        class Part
        {
            public string Prefix = "";            // v, a0, a1, s0 ...
            public MediaPlaylist Playlist = null!;
            public MediaTrack? Track;
            public string Output = "";
        }

        public async Task RunAsync(DownloadItem it, EngineContext ctx, CancellationToken ct)
        {
            var text = await Http.GetStringAsync(it.Url, it, ct);
            if (!text.TrimStart().StartsWith("#EXTM3U")) throw new InvalidDataException("Geçerli bir m3u8 dosyası değil");

            var parts = new List<Part>();
            bool embeddedAudio = true;     // ses video akışının içinde mi
            MediaTrack? embeddedTrack = null;

            if (text.Contains("#EXT-X-STREAM-INF"))
            {
                var master = ParseMaster(it.Url, text);
                var audios = TrackSelection.Resolve(master.Audio, it.AudioTracks, ctx.Settings, TrackKind.Audio);
                var subs = it.AudioOnly ? new List<MediaTrack>() : TrackSelection.Resolve(master.Subs, it.SubTracks, ctx.Settings, TrackKind.Subtitle);
                // Yalnızca ses modunda, ses ayrı playlist'lerdeyse görüntü hiç indirilmez
                bool needVideo = !it.AudioOnly || master.Audio.Count == 0 || audios.Any(a => a.Uri == null);

                if (needVideo)
                    parts.Add(new Part { Prefix = "v", Playlist = ParseMedia(master.VideoUrl, await Http.GetStringAsync(master.VideoUrl, it, ct)) });
                if (master.Audio.Count > 0)
                {
                    embeddedAudio = false;
                    int k = 0;
                    foreach (var a in audios)
                    {
                        if (a.Uri == null) { embeddedAudio = true; embeddedTrack = a; continue; } // video ile birlikte gelen ses
                        parts.Add(new Part { Prefix = "a" + k++, Track = a, Playlist = ParseMedia(a.Uri, await Http.GetStringAsync(a.Uri, it, ct)) });
                    }
                }
                int si = 0;
                foreach (var s in subs)
                {
                    try
                    {
                        var st = await Http.GetStringAsync(s.Uri!, it, ct);
                        var pl = st.TrimStart().StartsWith("#EXTM3U")
                            ? ParseMedia(s.Uri!, st)
                            : new MediaPlaylist { Segments = { new Seg { Uri = s.Uri! } } }; // doğrudan .vtt adresi
                        parts.Add(new Part { Prefix = "s" + si++, Track = s, Playlist = pl });
                    }
                    catch { /* altyazı alınamazsa video yine insin */ }
                }
            }
            else parts.Add(new Part { Prefix = "v", Playlist = ParseMedia(it.Url, text) });

            var work = Path.Combine(it.SaveDir, ".tdm_" + it.Id);
            Directory.CreateDirectory(work);

            var jobs = new List<(string file, Seg seg)>();
            foreach (var p in parts)
                for (int i = 0; i < p.Playlist.Segments.Count; i++)
                    jobs.Add((Path.Combine(work, $"{p.Prefix}_{i:D6}.seg"), p.Playlist.Segments[i]));

            it.HlsTotal = jobs.Count;
            int done = 0; long bytes = 0;
            foreach (var j in jobs) if (File.Exists(j.file)) { done++; bytes += new FileInfo(j.file).Length; }
            it.HlsDone = done;
            it.DownloadedBytes = bytes;

            foreach (var p in parts) await SaveInit(it, p.Playlist, Path.Combine(work, p.Prefix + "_init.mp4"), ct);

            var keys = new ConcurrentDictionary<string, Task<byte[]>>();
            using var sem = new SemaphoreSlim(Math.Clamp(ctx.Settings.Connections, 1, 32));
            using var failCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = failCts.Token;

            var tasks = jobs.Where(j => !File.Exists(j.file)).Select(async j =>
            {
                await sem.WaitAsync(token);
                try
                {
                    byte[] data = await FetchWithRetry(it, j.seg.Uri, j.seg.RangeLen, j.seg.RangeOff, token);
                    if (j.seg.KeyUri != null)
                    {
                        var key = await keys.GetOrAdd(j.seg.KeyUri, u => Http.GetBytesAsync(u, it, CancellationToken.None));
                        var iv = j.seg.Iv ?? SeqIv(j.seg.Sequence);
                        using var aes = Aes.Create();
                        aes.Key = key;
                        data = aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
                    }
                    await ctx.Limiter.WaitAsync(data.Length, token);
                    var part = j.file + ".part";
                    await File.WriteAllBytesAsync(part, data, token);
                    File.Move(part, j.file, true);
                    int d = Interlocked.Increment(ref done);
                    it.HlsDone = d;
                    long b = Interlocked.Add(ref it.DownloadedBytes, data.Length);
                    it.TotalBytes = (long)((double)b / d * jobs.Count); // tahmini boyut
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { failCts.Cancel(); throw; }
                finally { sem.Release(); }
            }).ToList();

            try { await Task.WhenAll(tasks); }
            catch
            {
                ct.ThrowIfCancellationRequested();
                var real = tasks.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!).FirstOrDefault(e => e is not OperationCanceledException);
                if (real != null) throw real;
                throw;
            }

            // ---- Parçaları birleştir ----
            it.Status = DownloadStatus.Processing;
            foreach (var p in parts)
            {
                var files = jobs.Where(j => Path.GetFileName(j.file).StartsWith(p.Prefix + "_")).Select(j => j.file);
                if (p.Prefix.StartsWith("s"))
                {
                    p.Output = Path.Combine(work, p.Prefix + ".vtt");
                    await MergeSubtitles(files, p.Output, ct);
                }
                else
                {
                    p.Output = Path.Combine(work, p.Prefix + (p.Playlist.InitUri != null ? ".mp4" : ".ts"));
                    await Concat(Path.Combine(work, p.Prefix + "_init.mp4"), files, p.Output, ct);
                }
            }

            var video = parts.FirstOrDefault(p => p.Prefix == "v");
            var audioParts = parts.Where(p => p.Prefix.StartsWith("a")).ToList();
            var subParts = parts.Where(p => p.Prefix.StartsWith("s")).ToList();
            // sayfadan yakalanan ayrı altyazı dosyaları (oynatıcı .vtt/.srt'yi kendisi yüklüyorsa)
            if (!it.AudioOnly)
            {
                var ext = SelectExternalSubs(it, ctx.Settings);
                if (ext.Count > 0) SubLog($"{it.FileName}: {ext.Count} harici altyazı seçili");
                subParts.AddRange(await FetchExternalSubs(it, ext, work, ct));
            }
            embeddedAudio &= video != null;
            int audioCount = audioParts.Count + (embeddedAudio ? 1 : 0);
            bool mkv = audioCount > 1 || subParts.Count > 0;   // çoklu iz → tek MKV

            var ffmpeg = Files.FindTool(ctx.Settings.FfmpegPath, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            Directory.CreateDirectory(it.SaveDir);
            string baseName = Path.GetFileNameWithoutExtension(it.FileName);
            string? final = null;

            if (it.AudioOnly)
            {
                final = await ExportAudioOnly(ffmpeg, video, embeddedAudio, embeddedTrack, audioParts, it.SaveDir, baseName, ct);
            }
            else
            {
                if (ffmpeg != null)
                {
                    final = Files.Unique(Path.Combine(it.SaveDir, baseName + (mkv ? ".mkv" : ".mp4")));
                    var args = BuildMuxArgs(video!, audioParts, subParts, embeddedAudio, embeddedTrack, mkv, final, true);
                    var (code, _) = await RunProcess(ffmpeg, args, ct);
                    if (code != 0 || !File.Exists(final))
                    {
                        // ses filtresi uyumsuzsa filtresiz dene
                        args = BuildMuxArgs(video!, audioParts, subParts, embeddedAudio, embeddedTrack, mkv, final, false);
                        (code, _) = await RunProcess(ffmpeg, args, ct);
                    }
                    if ((code != 0 || !File.Exists(final)) && subParts.Count > 0)
                    {
                        SubLog($"ffmpeg altyazılarla birleştiremedi, altyazısız deneniyor: {it.FileName}");
                        // altyazı dönüştürülemediyse altyazısız birleştir (klasöre ayrı dosya bırakmadan)
                        bool mkv2 = audioCount > 1;
                        try { File.Delete(final); } catch { }
                        final = Files.Unique(Path.Combine(it.SaveDir, baseName + (mkv2 ? ".mkv" : ".mp4")));
                        args = BuildMuxArgs(video!, audioParts, new List<Part>(), embeddedAudio, embeddedTrack, mkv2, final, !mkv2);
                        (code, _) = await RunProcess(ffmpeg, args, ct);
                    }
                    if (code != 0 || !File.Exists(final)) final = null;
                }
                final ??= MoveRaw(it, parts, baseName);
            }

            it.FileName = Path.GetFileName(final);
            it.TotalBytes = new FileInfo(final).Length;
            it.DownloadedBytes = it.TotalBytes;
            try { Directory.Delete(work, true); } catch { }
        }

        static List<string> BuildMuxArgs(Part video, List<Part> audios, List<Part> subs, bool embeddedAudio, MediaTrack? embeddedTrack, bool mkv, string output, bool bsf)
        {
            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", video.Output };
            foreach (var a in audios) args.AddRange(new[] { "-i", a.Output });
            foreach (var s in subs) args.AddRange(new[] { "-i", s.Output });

            args.AddRange(new[] { "-map", "0:v:0?" });
            var audioMeta = new List<MediaTrack?>();
            if (embeddedAudio) { args.AddRange(new[] { "-map", "0:a?" }); audioMeta.Add(embeddedTrack); }
            int input = 1;
            foreach (var a in audios) { args.AddRange(new[] { "-map", $"{input++}:a:0" }); audioMeta.Add(a.Track); }
            var subMeta = new List<MediaTrack?>();
            foreach (var s in subs) { args.AddRange(new[] { "-map", $"{input++}:s:0" }); subMeta.Add(s.Track); }

            args.AddRange(new[] { "-c:v", "copy", "-c:a", "copy" });
            args.AddRange(new[] { "-disposition:v:0", "default" });
            if (subs.Count > 0) args.AddRange(new[] { "-c:s", mkv ? "srt" : "mov_text" });
            if (bsf && !mkv) args.AddRange(new[] { "-bsf:a", "aac_adtstoasc" });

            for (int i = 0; i < audioMeta.Count; i++)
            {
                var t = audioMeta[i];
                if (t != null)
                {
                    args.AddRange(new[] { $"-metadata:s:a:{i}", "language=" + Langs.ToIso3(t.Language) });
                    if (!string.IsNullOrWhiteSpace(t.Name)) args.AddRange(new[] { $"-metadata:s:a:{i}", "title=" + t.Name });
                }
                args.AddRange(new[] { $"-disposition:a:{i}", i == 0 ? "default" : "0" });
            }
            for (int i = 0; i < subMeta.Count; i++)
            {
                var t = subMeta[i];
                if (t != null)
                {
                    args.AddRange(new[] { $"-metadata:s:s:{i}", "language=" + Langs.ToIso3(t.Language) });
                    if (!string.IsNullOrWhiteSpace(t.Name)) args.AddRange(new[] { $"-metadata:s:s:{i}", "title=" + t.Name });
                }
                args.AddRange(new[] { $"-disposition:s:{i}", "0" });
            }
            if (!mkv) args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(output);
            return args;
        }

        // ---------------- Harici altyazılar ----------------

        static readonly bool EncodingsReady = RegisterEncodings();
        static bool RegisterEncodings() { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); return true; }

        /// <summary>Hangi harici altyazıların ekleneceği: pencerede seçilenler, yoksa ayarlardaki diller (+ dili bilinmeyenler).</summary>
        public static List<MediaTrack> SelectExternalSubs(DownloadItem it, Settings s)
        {
            var ext = MediaTrack.FromExternal(it.ExternalSubs);
            if (ext.Count == 0 || it.AudioOnly) return new();
            if (it.SubTracks != null) return ext.Where(t => it.SubTracks.Contains(t.Key)).ToList();
            return TrackSelection.Resolve(ext, null, s, TrackKind.Subtitle);
        }

        /// <summary>Altyazı metnini çözer: UTF-8 değilse Türkçe Windows-1254 kabul edilir (Türk sitelerindeki .srt'lerin çoğu böyle).</summary>
        static string DecodeText(byte[] b)
        {
            _ = EncodingsReady;
            int start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return System.Text.Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return System.Text.Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try { return new System.Text.UTF8Encoding(false, true).GetString(b, start, b.Length - start); }
            catch (System.Text.DecoderFallbackException) { return System.Text.Encoding.GetEncoding(1254).GetString(b); }
        }

        static async Task<List<Part>> FetchExternalSubs(DownloadItem it, List<MediaTrack> subs, string work, CancellationToken ct)
        {
            var result = new List<Part>();
            Directory.CreateDirectory(work);
            int k = 0;
            foreach (var t in subs)
            {
                try
                {
                    var text = DecodeText(await GetSubBytes(it, t, t.Uri!, ct)).Replace("\r\n", "\n");
                    string path;
                    if (text.TrimStart().StartsWith("#EXTM3U"))
                    {
                        // altyazı playlist'i: parçaları indir, tek .vtt yap
                        var pl = ParseMedia(t.Uri!, text);
                        var files = new List<string>();
                        for (int i = 0; i < pl.Segments.Count; i++)
                        {
                            var f = Path.Combine(work, $"x{k}_{i:D5}.vtt");
                            await File.WriteAllTextAsync(f, DecodeText(await GetSubBytes(it, t, pl.Segments[i].Uri, ct)), ct);
                            files.Add(f);
                        }
                        path = Path.Combine(work, $"x{k}.vtt");
                        await MergeSubtitles(files, path, ct);
                    }
                    else
                    {
                        var trimmed = text.TrimStart('\uFEFF', ' ', '\n');
                        var ext = trimmed.StartsWith("WEBVTT") ? ".vtt" : text.Contains("[Script Info]") ? ".ass" : ".srt";
                        if (!text.Contains("-->") && ext != ".ass") { SubLog($"ATLANDI (altyazı değil): {t.Uri}\n{text[..Math.Min(200, text.Length)]}"); continue; }
                        if (text.Contains("#xywh=")) { SubLog($"ATLANDI (önizleme resmi): {t.Uri}"); continue; }
                        path = Path.Combine(work, $"x{k}{ext}");
                        await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false), ct);
                    }
                    result.Add(new Part { Prefix = "x" + k, Track = t, Output = path });
                    SubLog($"OK: {t.Uri} → {Path.GetFileName(path)} ({text.Length} karakter)");
                    k++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { SubLog($"HATA: {t.Uri} → {ex.Message}"); /* diğerlerine devam */ }
            }
            return result;
        }

        static void SubLog(string line)
        {
            try
            {
                var dir = Path.Combine(Settings.AppDataDir, "logs"); Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "subtitles.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
            }
            catch { }
        }

        /// <summary>
        /// Altyazı dosyasını indirir. Altyazı sunucuları genelde isteğin oynatıcı çerçevesinden (iframe) gelip gelmediğine
        /// bakar; sırayla oynatıcının adresi, film sayfası ve başlıksız istek denenir.
        /// </summary>
        static async Task<byte[]> GetSubBytes(DownloadItem it, MediaTrack t, string url, CancellationToken ct)
        {
            var src = t.Source;
            var attempts = new List<(string? referer, string? origin)>();
            if (!string.IsNullOrEmpty(src?.Referer) || !string.IsNullOrEmpty(src?.Origin)) attempts.Add((src!.Referer ?? src.Origin + "/", src.Origin));
            if (!string.IsNullOrEmpty(it.Referer)) attempts.Add((it.Referer, null));
            attempts.Add((null, null));
            Exception? last = null;
            foreach (var (referer, origin) in attempts)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(it.UserAgent) ? Http.DefaultUA : it.UserAgent);
                    req.Headers.TryAddWithoutValidation("Accept", "*/*");
                    if (referer != null) req.Headers.TryAddWithoutValidation("Referer", referer);
                    if (origin != null) req.Headers.TryAddWithoutValidation("Origin", origin);
                    var cookies = !string.IsNullOrEmpty(src?.Cookies) ? src!.Cookies : null;
                    if (cookies != null) req.Headers.TryAddWithoutValidation("Cookie", cookies);
                    using var t2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    t2.CancelAfter(TimeSpan.FromSeconds(30));
                    using var res = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, t2.Token);
                    if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase} (Referer: {referer ?? "yok"})");
                    var data = await res.Content.ReadAsByteArrayAsync(t2.Token);
                    var ce = res.Content.Headers.ContentEncoding;
                    if (ce.Contains("gzip")) data = Decompress(new System.IO.Compression.GZipStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress));
                    else if (ce.Contains("br")) data = Decompress(new System.IO.Compression.BrotliStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress));
                    else if (ce.Contains("deflate")) data = Decompress(new System.IO.Compression.ZLibStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress));
                    return data;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { last = ex; SubLog($"deneme başarısız: {url} → {ex.Message}"); }
            }
            throw last ?? new Exception("altyazı indirilemedi");
        }

        static byte[] Decompress(Stream s) { using (s) { var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); } }

        /// <summary>Doğrudan indirilen video dosyasına (mp4 vb.) seçilen harici altyazıları ekleyip MKV yapar.</summary>
        public static async Task<string> AttachExternalSubsAsync(DownloadItem it, EngineContext ctx, string videoPath, CancellationToken ct)
        {
            var subs = SelectExternalSubs(it, ctx.Settings);
            var ffmpeg = Files.FindTool(ctx.Settings.FfmpegPath, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            string[] videoExt = { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".ts", ".avi" };
            if (subs.Count == 0 || ffmpeg == null || !videoExt.Contains(Path.GetExtension(videoPath).ToLowerInvariant())) return videoPath;

            var work = Path.Combine(it.SaveDir, ".tdm_" + it.Id);
            try
            {
                var parts = await FetchExternalSubs(it, subs, work, ct);
                if (parts.Count == 0) return videoPath;
                it.Status = DownloadStatus.Processing;
                var tmp = Path.Combine(work, "out.mkv");
                var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", videoPath };
                foreach (var p in parts) args.AddRange(new[] { "-i", p.Output });
                args.AddRange(new[] { "-map", "0:v?", "-map", "0:a?" });
                for (int i = 0; i < parts.Count; i++) args.AddRange(new[] { "-map", $"{i + 1}:s:0" });
                args.AddRange(new[] { "-map", "0:s?", "-c", "copy", "-c:s", "srt" });
                for (int i = 0; i < parts.Count; i++)
                {
                    var t = parts[i].Track!;
                    if (t.Language.Length > 0) args.AddRange(new[] { $"-metadata:s:s:{i}", "language=" + Langs.ToIso3(t.Language) });
                    args.AddRange(new[] { $"-metadata:s:s:{i}", "title=" + (t.Language.Length > 0 ? Langs.DisplayName(t.Language) : t.Name) });
                    args.AddRange(new[] { $"-disposition:s:{i}", "0" });
                }
                args.Add(tmp);
                var (code, _) = await RunProcess(ffmpeg, args, ct);
                if (code != 0 || !File.Exists(tmp)) return videoPath;
                var target = Path.ChangeExtension(videoPath, ".mkv");
                File.Delete(videoPath);
                target = Files.Unique(target);
                File.Move(tmp, target);
                return target;
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        /// <summary>Yalnızca ses modu: seçilen her dublaj ayrı bir .m4a dosyası (birden fazlaysa "ad.tr.m4a", "ad.en.m4a").</summary>
        static async Task<string> ExportAudioOnly(string? ffmpeg, Part? video, bool embeddedAudio, MediaTrack? embeddedTrack,
            List<Part> audioParts, string dir, string baseName, CancellationToken ct)
        {
            var sources = new List<(string file, MediaTrack? track, string prefix)>();
            if (video != null && embeddedAudio) sources.Add((video.Output, embeddedTrack, "v"));
            foreach (var a in audioParts) sources.Add((a.Output, a.Track, a.Prefix));
            if (sources.Count == 0) throw new InvalidDataException("İndirilecek ses izi bulunamadı");

            string? first = null;
            var usedTags = new HashSet<string>();
            foreach (var (file, track, prefix) in sources)
            {
                var tag = track != null && track.Language.Length > 0 ? Langs.Normalize(track.Language) : prefix;
                if (!usedTags.Add(tag)) tag += "-" + prefix;
                var stem = baseName + (sources.Count > 1 ? "." + tag : "");
                string? outPath = null;
                if (ffmpeg != null)
                {
                    var meta = new List<string>();
                    if (track != null)
                    {
                        meta.AddRange(new[] { "-metadata:s:a:0", "language=" + Langs.ToIso3(track.Language) });
                        if (!string.IsNullOrWhiteSpace(track.Name)) meta.AddRange(new[] { "-metadata:s:a:0", "title=" + track.Name });
                    }
                    // önce .m4a (AAC), olmazsa filtresiz, o da olmazsa her codec'i kabul eden .mka
                    foreach (var (ext, bsf) in new[] { (".m4a", true), (".m4a", false), (".mka", false) })
                    {
                        var target = Files.Unique(Path.Combine(dir, stem + ext));
                        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", file, "-map", "0:a:0", "-vn", "-c:a", "copy" };
                        if (bsf) args.AddRange(new[] { "-bsf:a", "aac_adtstoasc" });
                        args.AddRange(meta);
                        args.Add(target);
                        var (code, _) = await RunProcess(ffmpeg, args, ct);
                        if (code == 0 && File.Exists(target)) { outPath = target; break; }
                        try { File.Delete(target); } catch { }
                    }
                }
                if (outPath == null)
                {
                    outPath = Files.Unique(Path.Combine(dir, stem + Path.GetExtension(file)));
                    File.Move(file, outPath);
                }
                first ??= outPath;
            }
            return first!;
        }

        /// <summary>ffmpeg yoksa ya da başarısızsa: video, ses ve altyazıları yan yana ayrı dosya olarak bırakır.</summary>
        static string MoveRaw(DownloadItem it, List<Part> parts, string baseName)
        {
            var v = parts[0];
            var final = Files.Unique(Path.Combine(it.SaveDir, baseName + Path.GetExtension(v.Output)));
            File.Move(v.Output, final);
            foreach (var p in parts.Skip(1))
            {
                if (!File.Exists(p.Output)) continue;
                var tag = p.Track != null && p.Track.Language.Length > 0 ? Langs.Normalize(p.Track.Language) : p.Prefix;
                var suffix = p.Prefix.StartsWith("s") ? $".{tag}" : $".ses-{tag}";
                File.Move(p.Output, Files.Unique(Path.Combine(it.SaveDir, baseName + suffix + Path.GetExtension(p.Output))));
            }
            return final;
        }

        /// <summary>HLS WebVTT parçalarını tek bir .vtt dosyasında birleştirir (tekrarlanan başlıkları ve aynı replikleri atar).</summary>
        static async Task MergeSubtitles(IEnumerable<string> files, string output, CancellationToken ct)
        {
            var sb = new System.Text.StringBuilder("WEBVTT\n\n");
            var seen = new HashSet<string>();
            foreach (var f in files)
            {
                var txt = (await File.ReadAllTextAsync(f, ct)).Replace("\r\n", "\n").TrimStart('﻿');
                foreach (var block in txt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    var b = block.Trim('\n');
                    if (b.StartsWith("WEBVTT") || b.StartsWith("X-TIMESTAMP-MAP") || b.StartsWith("NOTE") || b.StartsWith("STYLE") || b.StartsWith("REGION")) continue;
                    var lines = b.Split('\n').ToList();
                    int ti = lines.FindIndex(l => l.Contains("-->"));
                    if (ti < 0) continue;
                    var cue = string.Join("\n", lines.Skip(ti));       // cue numarasını/kimliğini at
                    if (seen.Add(cue)) sb.Append(cue).Append("\n\n");
                }
            }
            await File.WriteAllTextAsync(output, sb.ToString(), new System.Text.UTF8Encoding(false), ct);
        }

        static MediaPlaylist ParseMedia(string baseUrl, string text)
        {
            var pl = new MediaPlaylist();
            string? keyUri = null; byte[]? iv = null;
            long seq = 0; long? pendingLen = null, pendingOff = null; long lastEnd = 0;
            foreach (var raw in text.Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) long.TryParse(l.Substring(22), out seq);
                else if (l.StartsWith("#EXT-X-KEY"))
                {
                    var a = Attrs(l);
                    var method = a.GetValueOrDefault("METHOD", "NONE");
                    var fmt = a.GetValueOrDefault("KEYFORMAT", "identity");
                    if (method == "NONE") { keyUri = null; iv = null; }
                    else if (method == "AES-128" && fmt == "identity")
                    {
                        keyUri = Resolve(baseUrl, a["URI"]);
                        iv = a.TryGetValue("IV", out var ivs) ? HexIv(ivs) : null;
                    }
                    else throw new NotSupportedException("Bu yayın DRM korumalı (Widevine/FairPlay/SAMPLE-AES); indirilemez.");
                }
                else if (l.StartsWith("#EXT-X-MAP"))
                {
                    var a = Attrs(l);
                    pl.InitUri = Resolve(baseUrl, a["URI"]);
                    if (a.TryGetValue("BYTERANGE", out var br)) { var (ln, of) = ParseRange(br, 0); pl.InitLen = ln; pl.InitOff = of; }
                }
                else if (l.StartsWith("#EXT-X-BYTERANGE:"))
                {
                    var (ln, of) = ParseRange(l.Substring(17), lastEnd);
                    pendingLen = ln; pendingOff = of;
                }
                else if (!l.StartsWith("#"))
                {
                    var s = new Seg { Uri = Resolve(baseUrl, l), KeyUri = keyUri, Iv = iv, Sequence = seq++ };
                    if (pendingLen != null) { s.RangeLen = pendingLen; s.RangeOff = pendingOff; lastEnd = pendingOff!.Value + pendingLen.Value; pendingLen = null; }
                    pl.Segments.Add(s);
                }
            }
            if (pl.Segments.Count == 0) throw new InvalidDataException("Playlist'te parça yok");
            return pl;
        }

        static (long len, long off) ParseRange(string s, long defOff)
        {
            var p = s.Split('@');
            long len = long.Parse(p[0], CultureInfo.InvariantCulture);
            long off = p.Length > 1 ? long.Parse(p[1], CultureInfo.InvariantCulture) : defOff;
            return (len, off);
        }

        static byte[] HexIv(string s)
        {
            s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
            s = s.PadLeft(32, '0');
            return Convert.FromHexString(s);
        }

        static byte[] SeqIv(long seq)
        {
            var iv = new byte[16];
            for (int i = 15; i >= 8; i--) { iv[i] = (byte)(seq & 0xFF); seq >>= 8; }
            return iv;
        }

        static async Task SaveInit(DownloadItem it, MediaPlaylist pl, string path, CancellationToken ct)
        {
            if (pl.InitUri == null || File.Exists(path)) return;
            var data = await FetchWithRetry(it, pl.InitUri, pl.InitLen, pl.InitOff, ct);
            await File.WriteAllBytesAsync(path, data, ct);
        }

        static async Task<byte[]> FetchWithRetry(DownloadItem it, string url, long? len, long? off, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var req = len.HasValue ? Http.Build(HttpMethod.Get, url, it, off, off + len - 1) : Http.Build(HttpMethod.Get, url, it);
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    t.CancelAfter(TimeSpan.FromSeconds(60));
                    using var res = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, t.Token);
                    res.EnsureSuccessStatusCode();
                    return await res.Content.ReadAsByteArrayAsync(t.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch when (attempt < 6) { await Task.Delay(700 * (attempt + 1), ct); }
            }
        }

        static async Task Concat(string init, IEnumerable<string> parts, string output, CancellationToken ct)
        {
            await using var o = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
            if (File.Exists(init)) { await using var f = File.OpenRead(init); await f.CopyToAsync(o, ct); }
            foreach (var p in parts) { await using var f = File.OpenRead(p); await f.CopyToAsync(o, ct); }
        }

        public static async Task<(int code, string err)> RunProcess(string exe, IEnumerable<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var errTask = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            return (p.ExitCode, await errTask);
        }
    }
}
