using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using Newtonsoft.Json;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// 状态上报 producer：定时（默认 60s）从最近窗口的 device_snapshot 聚合每台设备的
    /// 状态值（按 EventStatMode：Avg/Max/Min/Median），入队 push_outbox 由 PushWorker 推送。
    /// 不重复入队：用 AppSetting 存最后一次处理的 snapshot id 作为游标。
    /// </summary>
    public sealed class StatusUploadWorker
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const string LastIdKey = "StatusUpload.LastSnapshotId";

        private readonly DeviceSnapshotRepository _snapRepo = new();
        private readonly DeviceRepository         _devRepo  = new();
        private readonly OutboxRepository         _outbox   = new();
        private readonly AppSettingRepository     _settings = new();

        private System.Threading.Timer _timer;
        private volatile bool _running;

        public bool Running => _timer != null;

        public void Start()
        {
            if (_timer != null) return;
            int interval = _settings.Get<int>(SK.StatusUploadIntervalSec, 60);
            _timer = new System.Threading.Timer(_ => SafeRun(),
                null, interval * 1000, interval * 1000);
            Log.Info("StatusUploadWorker 已启动，间隔 {0}s", interval);
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }

        public int RunOnce() => SafeRun();

        private int SafeRun()
        {
            if (_running) return 0;
            _running = true;
            try { return Run(); }
            catch (Exception ex) { Log.Error(ex, "StatusUploadWorker.Run 异常"); return 0; }
            finally { _running = false; }
        }

        private int Run()
        {
            string url = _settings.Get(SK.StatusApiUrl);
            if (string.IsNullOrWhiteSpace(url)) return 0;   // 未配 URL，跳过不 spam

            int interval = _settings.Get<int>(SK.StatusUploadIntervalSec, 60);
            string mode  = _settings.Get(SK.EventStatMode, "Avg");
            int maxRetry = _settings.Get<int>(SK.MaxRetryCount, 10);

            // 只处理"洗车机"启用设备
            var devs = _devRepo.GetAll(true).Where(d => d.DeviceType == "洗车机").ToList();
            if (devs.Count == 0) return 0;

            // 聚合窗口：[now - interval, now]
            DateTime to = DateTime.Now;
            DateTime from = to.AddSeconds(-interval);
            int enqueued = 0;

            foreach (var d in devs)
            {
                List<DeviceSnapshotEntity> window;
                try { window = _snapRepo.QueryRange(d.Id, from, to).ToList(); }
                catch (Exception ex) { Log.Warn(ex, "QueryRange 失败 dev={0}", d.Id); continue; }
                if (window.Count == 0) continue;

                var last = window[window.Count - 1];
                var payload = new
                {
                    deviceCode    = d.DeviceCode,
                    deviceType    = d.DeviceType,
                    time          = last.Time,
                    online        = last.Online,
                    startup       = last.Startup,
                    currents      = Aggregate(window.Select(s => s.Currents),      mode),
                    waterPressure = Aggregate(window.Select(s => s.WaterPressure), mode),
                    flowQuantity  = Aggregate(window.Select(s => s.FlowQuantity),  mode),
                    samples       = window.Count,
                    statMode      = mode
                };
                string json = JsonConvert.SerializeObject(payload);

                try
                {
                    _outbox.Enqueue("status", url, json, "device_snapshot", null, maxRetry);
                    enqueued++;
                }
                catch (Exception ex) { Log.Warn(ex, "Enqueue status 失败 dev={0}", d.Id); }
            }

            if (enqueued > 0)
                Log.Info("StatusUploadWorker：入队 {0} 条状态消息", enqueued);
            return enqueued;
        }

        private static double? Aggregate(IEnumerable<double?> xs, string mode)
        {
            var list = xs.Where(x => x.HasValue).Select(x => x.Value).ToList();
            if (list.Count == 0) return null;
            switch (mode?.ToLowerInvariant())
            {
                case "max":    return list.Max();
                case "min":    return list.Min();
                case "median":
                    var s = list.OrderBy(x => x).ToList();
                    int n = s.Count;
                    return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
                case "avg":
                default:       return list.Average();
            }
        }
    }
}
