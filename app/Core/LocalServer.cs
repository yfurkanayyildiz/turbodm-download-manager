using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TurboDM.Core
{
    /// <summary>
    /// Tarayıcı eklentisinin indirme göndermesi için yalnızca bu bilgisayardan erişilebilen küçük HTTP sunucu.
    /// Web sayfalarının istek atmasını engellemek için yalnızca eklenti kaynaklı (chrome-extension://) istekler kabul edilir.
    /// </summary>
    public class LocalServer : IDisposable
    {
        public const string Version = "1.0.0";
        readonly Func<DownloadRequest, Task> _onAdd;
        HttpListener? _listener;
        public int Port { get; private set; }
        public bool Running => _listener?.IsListening == true;
        public string? LastError { get; private set; }

        static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        public LocalServer(Func<DownloadRequest, Task> onAdd) => _onAdd = onAdd;

        public bool Start(int port)
        {
            Stop();
            Port = port;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                _ = Loop(_listener);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _listener = null;
                return false;
            }
        }

        public void Stop()
        {
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
        }

        async Task Loop(HttpListener l)
        {
            while (l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await l.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        static bool AllowedOrigin(string? origin) =>
            origin == null
            || origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase);

        async Task Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            try
            {
                var origin = req.Headers["Origin"];
                if (!AllowedOrigin(origin)) { await Reply(res, 403, new { ok = false, error = "forbidden origin" }); return; }
                if (origin != null)
                {
                    res.AddHeader("Access-Control-Allow-Origin", origin);
                    res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    res.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    res.AddHeader("Access-Control-Allow-Private-Network", "true");
                }
                if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

                var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
                if (path == "/ping") { await Reply(res, 200, new { ok = true, app = "TurboDM", version = Version }); return; }
                if (path == "/add" && req.HttpMethod == "POST")
                {
                    using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = await sr.ReadToEndAsync();
                    var dr = JsonSerializer.Deserialize<DownloadRequest>(body, JsonOpts);
                    if (dr == null || !Uri.TryCreate(dr.Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                    { await Reply(res, 400, new { ok = false, error = "geçersiz url" }); return; }
                    await _onAdd(dr);
                    await Reply(res, 200, new { ok = true });
                    return;
                }
                await Reply(res, 404, new { ok = false, error = "not found" });
            }
            catch (Exception ex)
            {
                try { await Reply(res, 500, new { ok = false, error = ex.Message }); } catch { }
            }
        }

        static async Task Reply(HttpListenerResponse res, int code, object body)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            res.StatusCode = code;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }

        public void Dispose() => Stop();
    }
}
