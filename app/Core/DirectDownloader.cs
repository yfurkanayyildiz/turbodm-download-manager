using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TurboDM.Core
{
    public class EngineContext
    {
        public Settings Settings { get; init; } = new();
        public SpeedLimiter Limiter { get; init; } = new();
    }

    public record ProbeResult(long Size, bool Resumable, string FinalUrl, string? FileName, string? ContentType);

    /// <summary>
    /// IDM tarzı çok bağlantılı indirici: dosyayı parçalara böler, her parçayı ayrı HTTP Range
    /// isteğiyle indirir, biten bağlantı en büyük kalan parçayı ikiye bölüp devralır (dinamik bölme).
    /// İlerleme parça bazında kaydedilir; duraklat/devam et ve uygulama yeniden açılınca devam desteklenir.
    /// </summary>
    public class DirectDownloader
    {
        const int BufferSize = 128 * 1024;
        const long MinSplit = 2L * 1024 * 1024;
        const int MaxRetries = 8;

        public static async Task<ProbeResult> ProbeAsync(DownloadItem it, CancellationToken ct)
        {
            using var req = Http.Build(HttpMethod.Get, it.Url, it, 0, 0);
            using var res = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"Sunucu {(int)res.StatusCode} {res.ReasonPhrase} döndürdü");

            var finalUrl = res.RequestMessage?.RequestUri?.ToString() ?? it.Url;
            long size = -1; bool resumable = false;
            if (res.StatusCode == HttpStatusCode.PartialContent && res.Content.Headers.ContentRange?.Length is long total)
            { size = total; resumable = true; }
            else if (res.Content.Headers.ContentLength is long len) size = len;

            string? name = null;
            var cd = res.Content.Headers.ContentDisposition;
            if (cd != null) name = (cd.FileNameStar ?? cd.FileName)?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(name)) name = Files.NameFromUrl(finalUrl);
            var mime = res.Content.Headers.ContentType?.MediaType;
            if (!Path.HasExtension(name) && Files.ExtFromMime(mime) is string ext) name += ext;
            return new ProbeResult(size, resumable, finalUrl, Files.Sanitize(name!), mime);
        }

        public async Task RunAsync(DownloadItem it, EngineContext ctx, CancellationToken ct)
        {
            var probe = await ProbeAsync(it, ct);

            if (probe.ContentType != null && probe.ContentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
            {
                it.Kind = DownloadKind.Hls;
                if (!it.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) it.FileName = Path.ChangeExtension(it.FileName, ".mp4");
                await new HlsDownloader().RunAsync(it, ctx, ct);
                return;
            }

            if (!it.NameLocked && Interlocked.Read(ref it.DownloadedBytes) == 0 && probe.FileName != null)
            {
                it.FileName = probe.FileName;
                if (!it.DirLocked) it.SaveDir = ctx.Settings.FolderFor(it.Category);
            }

            Directory.CreateDirectory(it.SaveDir);
            var tmp = it.FullPath + ".tdm";

            lock (it.SyncRoot)
            {
                bool sizeChanged = Interlocked.Read(ref it.TotalBytes) != probe.Size;
                if (sizeChanged || !probe.Resumable || !File.Exists(tmp)) it.Segments.Clear();
                it.TotalBytes = probe.Size;
                it.Resumable = probe.Resumable;
            }

            if (!probe.Resumable || probe.Size <= 0)
                await SingleStreamAsync(it, ctx, probe.FinalUrl, tmp, ct);
            else
                await MultiAsync(it, ctx, probe, tmp, ct);

            var final = Files.Unique(it.FullPath);
            File.Move(tmp, final);
            // oynatıcının ayrı yüklediği altyazılar yakalandıysa videoya ekle (MKV)
            final = await HlsDownloader.AttachExternalSubsAsync(it, ctx, final, ct);
            it.FileName = Path.GetFileName(final);
            lock (it.SyncRoot) it.Segments.Clear();
        }

        async Task SingleStreamAsync(DownloadItem it, EngineContext ctx, string url, string tmp, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                it.DownloadedBytes = 0;
                try
                {
                    using var req = Http.Build(HttpMethod.Get, attempt < 2 ? url : it.Url, it);
                    using var res = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    res.EnsureSuccessStatusCode();
                    await using var src = await res.Content.ReadAsStreamAsync(ct);
                    await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, true);
                    var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        while (true)
                        {
                            int read = await ReadWithTimeout(src, buf, ct);
                            if (read == 0) break;
                            await ctx.Limiter.WaitAsync(read, ct);
                            await dst.WriteAsync(buf.AsMemory(0, read), ct);
                            Interlocked.Add(ref it.DownloadedBytes, read);
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buf); }
                    long total = Interlocked.Read(ref it.TotalBytes);
                    if (total > 0 && Interlocked.Read(ref it.DownloadedBytes) < total) throw new IOException("Bağlantı erken kapandı");
                    if (total <= 0) it.TotalBytes = Interlocked.Read(ref it.DownloadedBytes);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch when (attempt < MaxRetries) { await Task.Delay(1000 * (attempt + 1), ct); }
            }
        }

        async Task MultiAsync(DownloadItem it, EngineContext ctx, ProbeResult probe, string tmp, CancellationToken ct)
        {
            long size = probe.Size;
            int conns = Math.Clamp(ctx.Settings.Connections, 1, 32);

            lock (it.SyncRoot)
            {
                if (it.Segments.Count == 0)
                {
                    int n = size < MinSplit ? 1 : (int)Math.Min(conns, size / (MinSplit / 2));
                    n = Math.Max(1, n);
                    long part = size / n;
                    for (int i = 0; i < n; i++)
                        it.Segments.Add(new Segment { Start = i * part, End = i == n - 1 ? size - 1 : (i + 1) * part - 1 });
                }
                foreach (var s in it.Segments) s.Active = false;
                it.DownloadedBytes = it.Segments.Sum(s => Interlocked.Read(ref s.Downloaded));
            }

            using var handle = File.OpenHandle(tmp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (RandomAccess.GetLength(handle) != size) RandomAccess.SetLength(handle, size);

            using var failCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = failCts.Token;

            Segment? TakeNext()
            {
                lock (it.SyncRoot)
                {
                    var free = it.Segments.FirstOrDefault(s => !s.Active && s.Remaining > 0);
                    if (free != null) { free.Active = true; return free; }

                    // Dinamik bölme: en çok kalanı olan aktif parçayı ikiye ayır
                    var big = it.Segments.Where(s => s.Active && s.Remaining > MinSplit).OrderByDescending(s => s.Remaining).FirstOrDefault();
                    if (big == null) return null;
                    long pos = big.Start + Interlocked.Read(ref big.Downloaded);
                    long rem = big.End - pos + 1;
                    long mid = pos + rem / 2;
                    var ns = new Segment { Start = mid, End = big.End, Active = true };
                    big.End = mid - 1;
                    it.Segments.Add(ns);
                    return ns;
                }
            }

            async Task Worker()
            {
                Segment? s;
                while ((s = TakeNext()) != null)
                {
                    try { await DownloadSegment(it, ctx, s, probe.FinalUrl, handle, token); }
                    finally { lock (it.SyncRoot) s.Active = false; }
                }
            }

            int workerCount;
            lock (it.SyncRoot) workerCount = Math.Min(conns, Math.Max(1, it.Segments.Count(s => s.Remaining > 0)));
            var tasks = Enumerable.Range(0, workerCount).Select(async _ =>
            {
                try { await Worker(); }
                catch { failCts.Cancel(); throw; }
            }).ToList();

            try { await Task.WhenAll(tasks); }
            catch
            {
                ct.ThrowIfCancellationRequested();
                var real = tasks.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!).FirstOrDefault(e => e is not OperationCanceledException);
                if (real != null) throw real;
                throw;
            }

            lock (it.SyncRoot)
                if (it.Segments.Any(s => s.Remaining > 0)) throw new IOException("İndirme tamamlanamadı");
        }

        async Task DownloadSegment(DownloadItem it, EngineContext ctx, Segment s, string url, Microsoft.Win32.SafeHandles.SafeFileHandle handle, CancellationToken ct)
        {
            int attempt = 0;
            var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    long pos, end;
                    lock (it.SyncRoot) { pos = s.Start + Interlocked.Read(ref s.Downloaded); end = s.End; }
                    if (pos > end) return;
                    try
                    {
                        using var req = Http.Build(HttpMethod.Get, attempt < 3 ? url : it.Url, it, pos, end);
                        using var res = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (res.StatusCode != HttpStatusCode.PartialContent)
                            throw new HttpRequestException($"Parça isteği reddedildi ({(int)res.StatusCode})");
                        await using var stream = await res.Content.ReadAsStreamAsync(ct);
                        while (true)
                        {
                            int read = await ReadWithTimeout(stream, buf, ct);
                            if (read == 0) break;
                            await ctx.Limiter.WaitAsync(read, ct);
                            long writePos; int toWrite;
                            lock (it.SyncRoot)
                            {
                                writePos = s.Start + Interlocked.Read(ref s.Downloaded);
                                toWrite = (int)Math.Min(read, s.End - writePos + 1);
                            }
                            if (toWrite <= 0) break;
                            // Bölme noktası her zaman tampon boyutundan uzakta olduğu için çakışma olmaz
                            RandomAccess.Write(handle, new ReadOnlySpan<byte>(buf, 0, toWrite), writePos);
                            Interlocked.Add(ref s.Downloaded, toWrite);
                            Interlocked.Add(ref it.DownloadedBytes, toWrite);
                            attempt = 0;
                            if (toWrite < read) break; // parça bölündü, bize düşen kısım bitti
                        }
                        lock (it.SyncRoot) if (s.Remaining <= 0) return;
                        throw new IOException("Bağlantı erken kapandı");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (HttpRequestException hx) when (hx.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone && attempt >= 3) { throw; }
                    catch when (attempt++ < MaxRetries) { await Task.Delay(800 * attempt, ct); }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        static async Task<int> ReadWithTimeout(Stream s, byte[] buf, CancellationToken ct)
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(30));
            try { return await s.ReadAsync(buf.AsMemory(0, buf.Length), t.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Sunucu yanıt vermiyor (zaman aşımı)"); }
        }
    }
}
