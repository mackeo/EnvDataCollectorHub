using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// push_outbox 消费者：定时取 Pending/Failed 待重试 → POST → 成功 MarkSuccess，
    /// 失败按 Fixed/Exponential 退避调用 MarkFailed(retryCount+1, nextRetry)。
    /// 单条异常隔离，不影响其他条。
    /// </summary>
    public sealed class PushWorker
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const int MaxBackoffSec = 3600;   // 指数退避封顶 1 小时

        private readonly OutboxRepository    _repo     = new();
        private readonly AppSettingRepository _settings = new();

        private TokenService _token;
        private System.Threading.Timer _timer;
        private volatile bool _running;

        public bool Running => _timer != null;

        public void Start(TokenService token)
        {
            if (_timer != null) return;
            _token = token;
            int interval = _settings.Get<int>(SK.RetryIntervalSec, 10);
            _timer = new System.Threading.Timer(_ => SafeRun(),
                null, 2000, interval * 1000);
            Log.Info("PushWorker 已启动，间隔 {0}s", interval);
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            _token = null;
        }

        /// <summary>手动触发一轮（Dashboard / OutboxPanel 用）。返回成功条数。</summary>
        public int RunOnce() => SafeRun();

        private int SafeRun()
        {
            if (_running) return 0;
            _running = true;
            try { return Run(); }
            catch (Exception ex) { Log.Error(ex, "PushWorker.Run 异常"); return 0; }
            finally { _running = false; }
        }

        private int Run()
        {
            int batchSize  = _settings.Get<int>(SK.RetryBatchSize, 20);
            int timeoutSec = _settings.Get<int>(SK.HttpTimeoutSec, 30);
            int baseSec    = _settings.Get<int>(SK.RetryIntervalSec, 10);
            string backoff = _settings.Get(SK.RetryBackoff, "Exponential");
            bool tokenEnabled = _settings.Get<int>(SK.TokenEnabled, 0) == 1;

            var batch = _repo.DequeueDue(batchSize).ToList();
            if (batch.Count == 0) return 0;

            int ok = 0;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };

            foreach (var msg in batch)
            {
                if (string.IsNullOrWhiteSpace(msg.TargetUrl))
                {
                    var nrt = NextRetry(backoff, baseSec, msg.RetryCount);
                    _repo.MarkFailed(msg.Id, null, "TargetUrl 为空", msg.RetryCount + 1, nrt);
                    continue;
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, msg.TargetUrl)
                    {
                        Content = new StringContent(msg.PayloadJson ?? "", Encoding.UTF8, "application/json")
                    };
                    if (tokenEnabled && _token != null)
                    {
                        if (!_token.ApplyBearer(req))
                        {
                            // token 拿不到：保留 retry_count 不递增，短延后重试避免 spam
                            _repo.MarkFailed(msg.Id, null, "Token 不可用：" + (_token.LastError ?? ""),
                                msg.RetryCount, DateTime.Now.AddSeconds(baseSec));
                            continue;
                        }
                    }

                    var resp = http.SendAsync(req).GetAwaiter().GetResult();
                    int code = (int)resp.StatusCode;
                    string respText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (code >= 200 && code < 300)
                    {
                        _repo.MarkSuccess(msg.Id);
                        ok++;
                    }
                    else
                    {
                        var nrt = NextRetry(backoff, baseSec, msg.RetryCount);
                        _repo.MarkFailed(msg.Id, code, Truncate(respText, 500),
                            msg.RetryCount + 1, nrt);
                    }
                }
                catch (Exception ex)
                {
                    var nrt = NextRetry(backoff, baseSec, msg.RetryCount);
                    _repo.MarkFailed(msg.Id, null, Truncate(ex.Message, 500),
                        msg.RetryCount + 1, nrt);
                }
            }

            if (batch.Count > 0)
                Log.Info("PushWorker：取出 {0} 条，成功 {1}", batch.Count, ok);
            return ok;
        }

        private static DateTime NextRetry(string backoff, int baseSec, int retryCount)
        {
            int sec;
            if (string.Equals(backoff, "Exponential", StringComparison.OrdinalIgnoreCase))
            {
                int shift = Math.Min(20, retryCount);
                long val = (long)baseSec * (1L << shift);
                sec = (int)Math.Min(val, MaxBackoffSec);
            }
            else { sec = baseSec; }
            return DateTime.Now.AddSeconds(sec);
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));
    }
}
