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
    /// 状态上报 producer：定时（默认 60s）从最近窗口的 variable_trend 按 var_role 聚合
    /// 每台设备的状态值（max/min/median/last），online/startup 取最新值；
    /// 入队 push_outbox 由 PushWorker 推送。未配 StatusApiUrl 时跳过不入队。
    /// </summary>
    public sealed class StatusUploadWorker
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly VariableTrendRepository  _trendRepo = new();
        private readonly DeviceRepository         _devRepo   = new();
        private readonly OutboxRepository         _outbox    = new();
        private readonly AppSettingRepository     _settings  = new();

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
            if (string.IsNullOrWhiteSpace(url)) return 0;

            int interval = _settings.Get<int>(SK.StatusUploadIntervalSec, 60);
            string mode  = _settings.Get(SK.EventStatMode, "Avg");
            int maxRetry = _settings.Get<int>(SK.MaxRetryCount, 10);

            var devs = _devRepo.GetAll(true).Where(d => d.DeviceType == "洗车机").ToList();
            if (devs.Count == 0) return 0;

            DateTime to = DateTime.Now;
            DateTime from = to.AddSeconds(-interval);
            int enqueued = 0;

            foreach (var d in devs)
            {
                // 窗口内三个数值变量的统计（max/min/median + last）
                VariableTrendStats curr, press, flow;
                try
                {
                    curr  = _trendRepo.GetStats(d.Id, nameof(VarRole.Currents),      from, to);
                    press = _trendRepo.GetStats(d.Id, nameof(VarRole.WaterPressure), from, to);
                    flow  = _trendRepo.GetStats(d.Id, nameof(VarRole.FlowQuantity),  from, to);
                }
                catch (Exception ex) { Log.Warn(ex, "GetStats 失败 dev={0}", d.Id); continue; }

                // online/startup 取最新值（不在窗口约束，反映"当前"状态）
                int? startup = null;
                var lastStartup = _trendRepo.GetLatest(d.Id, nameof(VarRole.Startup));
                if (lastStartup != null && int.TryParse(lastStartup.ValueStr, out int sv))
                    startup = sv;

                // 没收到任何变量更新就跳过（避免空 spam）
                if (curr.Last == null && press.Last == null && flow.Last == null && startup == null) continue;

                double? currVal  = mode.Equals("Max", StringComparison.OrdinalIgnoreCase) ? curr.Max
                                  : mode.Equals("Min", StringComparison.OrdinalIgnoreCase) ? curr.Min
                                  : mode.Equals("Avg", StringComparison.OrdinalIgnoreCase) ? curr.Avg
                                  : mode.Equals("Median", StringComparison.OrdinalIgnoreCase) ? curr.Median
                                  : curr.Last;
                double? pressVal = mode.Equals("Max", StringComparison.OrdinalIgnoreCase) ? press.Max
                                  : mode.Equals("Min", StringComparison.OrdinalIgnoreCase) ? press.Min
                                  : mode.Equals("Avg", StringComparison.OrdinalIgnoreCase) ? press.Avg
                                  : mode.Equals("Median", StringComparison.OrdinalIgnoreCase) ? press.Median
                                  : press.Last;
                double? flowVal  = mode.Equals("Max", StringComparison.OrdinalIgnoreCase) ? flow.Max
                                  : mode.Equals("Min", StringComparison.OrdinalIgnoreCase) ? flow.Min
                                  : mode.Equals("Avg", StringComparison.OrdinalIgnoreCase) ? flow.Avg
                                  : mode.Equals("Median", StringComparison.OrdinalIgnoreCase) ? flow.Median
                                  : flow.Last;

                var payload = new
                {
                    deviceCode    = d.DeviceCode,
                    deviceType    = d.DeviceType,
                    time          = to.ToString("yyyy-MM-dd HH:mm:ss"),
                    online = 1,           // 能产生 trend 就视为在线；OPC 断线时 online 由 status 历史推断
                    startup = startup,
                    currents      = currVal,
                    currentsMax   = curr.Max,
                    currentsMin   = curr.Min,
                    currentsAvg   = curr.Avg,
                    currentsMedian = curr.Median,
                    waterPressure = pressVal,
                    waterPressureMax = press.Max,
                    waterPressureMin = press.Min,
                    waterPressureAvg = press.Avg,
                    waterPressureMedian = press.Median,
                    flowQuantity  = flowVal,
                    flowQuantityMax = flow.Max,
                    flowQuantityMin = flow.Min,
                    flowQuantityAvg = flow.Avg,
                    flowQuantityMedian = flow.Median,
                    statMode      = mode
                };
                string json = JsonConvert.SerializeObject(payload);

                try
                {
                    _outbox.Enqueue("status", url, json, "variable_trend", null, maxRetry);
                    enqueued++;
                }
                catch (Exception ex) { Log.Warn(ex, "Enqueue status 失败 dev={0}", d.Id); }
            }

            if (enqueued > 0)
                Log.Info("StatusUploadWorker：入队 {0} 条状态消息", enqueued);
            return enqueued;
        }
    }
}
