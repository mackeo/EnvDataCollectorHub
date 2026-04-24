using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// 轻量级内置 HTTP 静态文件服务，用于对外暴露本地图片目录。
    /// 请求形如 GET /images/{deviceCode}/{yyyyMMdd}/{file.jpg}，映射到磁盘 {rootDir}/{deviceCode}/... 。
    /// 端口被占用或 "+" 无权限时会降级到 localhost，不阻塞程序启动。
    /// </summary>
    public sealed class ImageHttpServer : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly string _rootDir;
        private readonly string _urlPrefix;  // 形如 http://+:8088/images/
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _loop;

        public bool Running => _listener != null && _listener.IsListening;
        public string RootDirectory => _rootDir;
        public int Port { get; private set; }

        /// <summary>
        /// <paramref name="urlPrefix"/> 必须以 / 结尾，例如 "http://+:8088/images/"。
        /// <paramref name="rootDir"/> 相对 exe 或绝对路径均可。
        /// </summary>
        public ImageHttpServer(string urlPrefix = "http://+:8088/images/", string rootDir = "images")
        {
            if (!urlPrefix.EndsWith("/")) urlPrefix += "/";
            _urlPrefix = urlPrefix;
            _rootDir = Path.IsPathRooted(rootDir)
                ? rootDir
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rootDir);
            Directory.CreateDirectory(_rootDir);
        }

        public void Start()
        {
            if (Running) return;
            Port = ExtractPort(_urlPrefix);
            _listener = TryBind(_urlPrefix) ?? TryBind(_urlPrefix.Replace("+", "localhost"));
            if (_listener == null)
            {
                Log.Warn("ImageHttpServer 启动失败：端口 {0} 被占用或权限不足", Port);
                return;
            }
            _cts  = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoop(_cts.Token));
            Log.Info("ImageHttpServer 已启动：{0}，根目录={1}", _urlPrefix, _rootDir);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            try { _loop?.Wait(500); } catch { }
            _listener = null;
            _cts = null;
            _loop = null;
        }

        public void Dispose() => Stop();

        private static HttpListener TryBind(string prefix)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add(prefix);
                l.Start();
                return l;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ImageHttpServer bind {0} 失败", prefix);
                return null;
            }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => Handle(ctx), ct);
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                if (!string.Equals(ctx.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                { ctx.Response.StatusCode = 405; return; }

                string absPath = ctx.Request.Url.AbsolutePath;  // 例 /images/xxx/yyy.jpg
                string prefixPath = new Uri(_urlPrefix.Replace("+", "localhost")).AbsolutePath;
                if (!absPath.StartsWith(prefixPath, StringComparison.OrdinalIgnoreCase))
                { ctx.Response.StatusCode = 404; return; }

                string rel = absPath.Substring(prefixPath.Length)
                                    .Replace('/', Path.DirectorySeparatorChar);
                rel = Uri.UnescapeDataString(rel);

                // 防目录穿越
                string full = Path.GetFullPath(Path.Combine(_rootDir, rel));
                string rootFull = Path.GetFullPath(_rootDir);
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                { ctx.Response.StatusCode = 403; return; }

                if (!File.Exists(full))
                { ctx.Response.StatusCode = 404; return; }

                ctx.Response.ContentType = GuessContentType(full);
                using var fs = File.OpenRead(full);
                ctx.Response.ContentLength64 = fs.Length;
                fs.CopyTo(ctx.Response.OutputStream);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ImageHttpServer handle 异常");
                try { ctx.Response.StatusCode = 500; } catch { }
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private static string GuessContentType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png":               return "image/png";
                case ".gif":               return "image/gif";
                case ".bmp":               return "image/bmp";
                case ".webp":              return "image/webp";
                default:                   return "application/octet-stream";
            }
        }

        private static int ExtractPort(string prefix)
        {
            try
            {
                var u = new Uri(prefix.Replace("+", "localhost"));
                return u.Port;
            }
            catch { return 0; }
        }
    }
}
