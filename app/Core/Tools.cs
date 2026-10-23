using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TurboDM.Core
{
    public static class Tools
    {
        const string FfmpegZip = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

        public static string? FindFfmpeg(Settings s) => Files.FindTool(s.FfmpegPath, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

        /// <summary>ffmpeg'i (yt-dlp ekibinin Windows derlemesi) indirip AppData\TurboDM\bin içine çıkarır.</summary>
        public static async Task<string> InstallFfmpegAsync(IProgress<double>? progress, CancellationToken ct)
        {
            var bin = Path.Combine(Settings.AppDataDir, "bin");
            Directory.CreateDirectory(bin);
            var zipPath = Path.Combine(bin, "ffmpeg.zip");
            using (var res = await Http.Client.GetAsync(FfmpegZip, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? -1;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buf = new byte[1 << 16]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report(100.0 * done / total);
                }
            }
            string? result = null;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var e = zip.Entries.FirstOrDefault(x => x.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase));
                    if (e == null) continue;
                    var target = Path.Combine(bin, name);
                    e.ExtractToFile(target, true);
                    if (name == "ffmpeg.exe") result = target;
                }
            }
            File.Delete(zipPath);
            return result ?? throw new FileNotFoundException("Arşivde ffmpeg.exe bulunamadı");
        }

        const string DenoZip = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
        static readonly SemaphoreSlim DenoLock = new(1, 1);
        static bool _denoFailed;

        /// <summary>yt-dlp'nin YouTube imza çözümü için Deno'yu (tek exe) AppData\TurboDM\bin içine kurar. Hata olursa sessizce geçer.</summary>
        public static async Task TryEnsureDenoAsync(CancellationToken ct)
        {
            if (!OperatingSystem.IsWindows() || _denoFailed) return;
            if (Files.FindTool(null, "deno.exe") != null) return;
            await DenoLock.WaitAsync(ct);
            try
            {
                if (Files.FindTool(null, "deno.exe") != null) return;
                var bin = Path.Combine(Settings.AppDataDir, "bin");
                Directory.CreateDirectory(bin);
                var zipPath = Path.Combine(bin, "deno.zip");
                using (var res = await Http.Client.GetAsync(DenoZip, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    res.EnsureSuccessStatusCode();
                    await using var f = File.Create(zipPath);
                    await res.Content.CopyToAsync(f, ct);
                }
                using (var zip = ZipFile.OpenRead(zipPath))
                    zip.Entries.First(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase)).ExtractToFile(Path.Combine(bin, "deno.exe"), true);
                File.Delete(zipPath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { _denoFailed = true; }
            finally { DenoLock.Release(); }
        }
    }
}
