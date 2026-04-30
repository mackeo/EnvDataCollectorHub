using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EnvDataCollector.Data.Repositories;
using Newtonsoft.Json.Linq;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// JWT Token 缓存与自动续期。线程安全，所有请求共用一份 token。
    /// 失败不抛异常，IsValid=false，PushWorker 检测到无 token 时跳过该次请求保留为 Pending。
    /// </summary>
    public sealed class TokenService
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const int RefreshLeadSec = 300;   // 过期前 5 分钟预刷新

        private readonly AppSettingRepository _settings = new();
        private readonly object _lock = new();

        private string    _token;
        private DateTime? _expiresAt;
        private string    _lastError;

        public bool      IsValid    { get { lock (_lock) return !string.IsNullOrEmpty(_token) && (!_expiresAt.HasValue || _expiresAt.Value > DateTime.Now.AddSeconds(10)); } }
        public DateTime? ExpiresAt  { get { lock (_lock) return _expiresAt; } }
        public string    LastError  { get { lock (_lock) return _lastError; } }

        /// <summary>取当前 token；若过期或快过期自动刷新。失败返回 null。</summary>
        public string GetToken()
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(_token) &&
                    (!_expiresAt.HasValue || _expiresAt.Value > DateTime.Now.AddSeconds(RefreshLeadSec)))
                    return _token;
            }
            // 过期/未取过 → 刷新一次
            Refresh();
            lock (_lock) return _token;
        }

        /// <summary>主动刷新 token，返回是否成功。</summary>
        public bool Refresh()
        {
            string url      = _settings.Get(SK.TokenApiUrl);
            string user     = _settings.Get(SK.TokenUsername);
            string passEnc  = _settings.Get(SK.TokenPassword);
            int    timeout  = _settings.Get<int>(SK.HttpTimeoutSec, 30);

            if (string.IsNullOrWhiteSpace(url))
            {
                lock (_lock) _lastError = "TokenApiUrl 未配置";
                return false;
            }

            string pass = string.IsNullOrEmpty(passEnc) ? "" : CryptoHelper.Decrypt(passEnc);
            string body = new JObject { ["clientId"] = user, ["secret"] = pass }.ToString();

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                var resp = http.SendAsync(req).GetAwaiter().GetResult();
                string respText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    lock (_lock) _lastError = $"HTTP {(int)resp.StatusCode} {respText?.Substring(0, Math.Min(200, respText?.Length ?? 0))}";
                    Log.Warn("Token 刷新失败：{0}", _lastError);
                    return false;
                }

                var (token, expires) = ParseTokenResponse(respText);
                if (string.IsNullOrEmpty(token))
                {
                    lock (_lock) _lastError = "响应未找到 token 字段：" + respText?.Substring(0, Math.Min(200, respText?.Length ?? 0));
                    Log.Warn("Token 解析失败：{0}", _lastError);
                    return false;
                }

                lock (_lock)
                {
                    _token     = token;
                    _expiresAt = expires;
                    _lastError = null;
                }
                Log.Info("Token 已刷新，过期 {0}", expires?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(未知)");
                return true;
            }
            catch (Exception ex)
            {
                lock (_lock) _lastError = ex.Message;
                Log.Warn(ex, "Token 刷新异常");
                return false;
            }
        }

        private static (string token, DateTime? expires) ParseTokenResponse(string json)
        {
            JObject root;
            try { root = JObject.Parse(json); }
            catch { return (null, null); }

            string token = FindFirstString(root, "token", "access_token", "accessToken", "Token");
            if (string.IsNullOrEmpty(token))
            {
                var dataToken = root["data"];
                if (dataToken != null && dataToken.Type == JTokenType.Object)
                {
                    token = FindFirstString((JObject)dataToken, "data_0", "token", "access_token");
                }
            }
            if (string.IsNullOrEmpty(token)) return (null, null);

            // 优先绝对时间
            string expStr = FindFirstString(root, "expireTime", "expire_time", "expiresAt", "expires_at");
            if (!string.IsNullOrEmpty(expStr) && DateTime.TryParse(expStr, out var dt))
                return (token, dt);

            // 相对秒数
            int? sec = FindFirstInt(root, "expireSec", "expires_in", "expireIn", "ttl");
            if (sec.HasValue && sec.Value > 0)
                return (token, DateTime.Now.AddSeconds(sec.Value));

            // 没有过期信息：默认时长
            // qtcom 2小时过期
            return (token, DateTime.Now.AddMinutes(36));
        }

        private static string FindFirstString(JObject root, params string[] names)
        {
            foreach (var name in names)
            {
                var t = FindToken(root, name);
                if (t != null && t.Type != JTokenType.Null) return t.ToString();
            }
            return null;
        }

        private static int? FindFirstInt(JObject root, params string[] names)
        {
            foreach (var name in names)
            {
                var t = FindToken(root, name);
                if (t == null) continue;
                if (t.Type == JTokenType.Integer) return (int)t;
                if (int.TryParse(t.ToString(), out int v)) return v;
            }
            return null;
        }

        // 递归查找 key（深度 ≤ 3，足够覆盖 {data:{token:...}} 之类的嵌套）
        private static JToken FindToken(JToken node, string name, int depth = 3)
        {
            if (node is JObject obj)
            {
                if (obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var v)) return v;
                if (depth <= 0) return null;
                foreach (var p in obj.Properties())
                {
                    var found = FindToken(p.Value, name, depth - 1);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>给 HttpRequestMessage 加 jwt-token 自定义头，若 token 无效则不加。</summary>
        public bool ApplyBearer(HttpRequestMessage req)
        {
            string t = GetToken();
            if (string.IsNullOrEmpty(t)) return false;
            req.Headers.Add("jwt-token", t);
            return true;
        }
    }
}
