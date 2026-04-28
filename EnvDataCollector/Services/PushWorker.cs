using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDataCollector.Data;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// push_outbox 消费者：定时取 Pending/Failed 待重试 → POST → 成功 MarkSuccess，
    /// 失败按 Fixed/Exponential 退避调用 MarkFailed(retryCount+1, nextRetry)。
    /// 推送 message_type=event/related_table=run_record 之前会先检查 run_record 表当前
    /// vehicle_pic / vehicle_no_pic 是否为空，若空且本地 *_pic_local 有图则调
    /// ImageUploadService 重传，回填 run_record 并刷新 outbox 的 payload_json。
    /// </summary>
    public sealed class PushWorker
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const int MaxBackoffSec = 3600;   // 指数退避封顶 1 小时

        private readonly OutboxRepository      _repo      = new();
        private readonly AppSettingRepository  _settings  = new();
        private readonly RunRecordRepository   _runRepo   = new();

        private TokenService        _token;
        private ImageUploadService  _imageUploader;
        private System.Threading.Timer _timer;
        private volatile bool _running;

        public bool Running => _timer != null;

        public void Start(TokenService token, ImageUploadService imageUploader = null)
        {
            if (_timer != null) return;
            _token = token;
            _imageUploader = imageUploader;
            int interval = _settings.Get<int>(SK.RetryIntervalSec, 10);
            _timer = new System.Threading.Timer(_ => SafeRun(),
                null, 2000, interval * 1000);
            Log.Info("PushWorker 已启动，间隔 {0}s，图片补传 {1}",
                interval, imageUploader != null ? "已启用" : "未启用");
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            _token = null;
            _imageUploader = null;
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
            int timeoutSec = _settings.Get<int>(SK.HttpTimeoutSec, 15);
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

                // 推 event 前先看 run_record 现状，url 空且本地有图则补传 + 刷新 payload
                string payload = msg.PayloadJson ?? "";
                if (string.Equals(msg.MessageType, "event", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(msg.RelatedTable, "run_record", StringComparison.OrdinalIgnoreCase) &&
                    msg.RelatedId.HasValue && _imageUploader != null)
                {
                    string updated = TryRefreshRunRecordImages(msg.RelatedId.Value, payload);
                    if (!ReferenceEquals(updated, payload) && !string.IsNullOrEmpty(updated))
                    {
                        payload = updated;
                        try { _repo.UpdatePayload(msg.Id, payload); }
                        catch (Exception ex) { Log.Debug(ex, "刷新 outbox.payload 失败 id={0}", msg.Id); }
                    }
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, msg.TargetUrl)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
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

        /// <summary>
        /// 从 run_record 表读取最新行，若 vehicle_pic / vehicle_no_pic 为空且本地路径有图，
        /// 上传后回填 run_record 并返回新的 payload JSON；否则返回原 payload。
        /// </summary>
        private string TryRefreshRunRecordImages(long runRecordId, string originalPayload)
        {
            try
            {
                var rec = _runRepo.GetById(runRecordId);
                if (rec == null) return originalPayload;

                bool needVehicle = string.IsNullOrEmpty(rec.VehiclePic)   && !string.IsNullOrEmpty(rec.VehiclePicLocal);
                bool needPlate   = string.IsNullOrEmpty(rec.VehicleNoPic) && !string.IsNullOrEmpty(rec.VehicleNoPicLocal);
                if (!needVehicle && !needPlate) return originalPayload;

                bool changed = false;
                if (needVehicle)
                {
                    string url = _imageUploader.UploadFromLocal(rec.VehiclePicLocal);
                    if (!string.IsNullOrEmpty(url)) { rec.VehiclePic = url; changed = true; }
                }
                if (needPlate)
                {
                    string url = _imageUploader.UploadFromLocal(rec.VehicleNoPicLocal);
                    if (!string.IsNullOrEmpty(url)) { rec.VehicleNoPic = url; changed = true; }
                }
                if (!changed) return originalPayload;

                // 回填 run_record 表
                try { _runRepo.UpdateImageUrls(runRecordId, rec.VehiclePic, rec.VehicleNoPic); }
                catch (Exception ex) { Log.Debug(ex, "回填 run_record url 失败 id={0}", runRecordId); }

                // 刷新 payload：把原 payload 解析、覆盖 vehiclePic/vehicleNoPic 字段、再序列化
                try
                {
                    var obj = JObject.Parse(originalPayload);
                    obj["VehiclePic"]   = rec.VehiclePic;
                    obj["VehicleNoPic"] = rec.VehicleNoPic;
                    return obj.ToString(Formatting.None);
                }
                catch
                {
                    // 原 payload 不是 JSON 或解析失败 → 用 RunRecord 重新序列化兜底
                    return JsonConvert.SerializeObject(rec);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "TryRefreshRunRecordImages id={0} 异常", runRecordId);
                return originalPayload;
            }
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
