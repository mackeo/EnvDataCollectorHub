using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using EnvDataCollector.Services.Hk;
using Newtonsoft.Json;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// 状态上报 producer：定时（默认 60s）从最近窗口的 variable_trend 按 EventStatMode
    /// 计算单个统计值（currents/waterPressure（固定最大值）/flowQuantity），online （启停开关量的状态、opc、洗车机的摄像头连接状态）/ startup 取最新值；
    /// 入队 push_outbox 由 PushWorker 推送。未配 StatusApiUrl 时跳过不入队。
    /// </summary>
    public sealed class StatusUploadWorker
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly VariableTrendRepository  _trendRepo = new();
        private readonly DeviceRepository         _devRepo   = new();
        private readonly DeviceVariableRepository _varRepo   = new();
        private readonly OutboxRepository         _outbox    = new();
        private readonly AppSettingRepository     _settings  = new();

        private OpcUaService  _opc;
        private TrendWriter   _trendWriter;
        private CameraService _cam;

        private System.Threading.Timer _timer;
        private volatile bool _running;

        public bool Running => _timer != null;

        public void Start(OpcUaService opc = null, TrendWriter trendWriter = null, CameraService cam = null)
        {
            if (_timer != null) return;
            _opc = opc;
            _trendWriter = trendWriter;
            _cam = cam;
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
            int maxRetry = _settings.Get<int>(SK.MaxRetryCount, 5);

            var devs = _devRepo.GetAll(true).ToList();
            if (devs.Count == 0) return 0;

            DateTime to = DateTime.Now;
            DateTime from = to.AddSeconds(-interval);
            var items = new List<object>();

            foreach (var d in devs)
            {
                double? currVal = null, pressVal = null, flowVal = null;
                try
                {
                    currVal  = _trendRepo.GetStat(d.Id, nameof(VarRole.Currents),      from, to, mode);
                    pressVal = _trendRepo.GetStat(d.Id, nameof(VarRole.WaterPressure), from, to, "Max");
                    flowVal  = _trendRepo.GetStat(d.Id, nameof(VarRole.FlowQuantity),  from, to, mode);
                }
                catch (Exception ex) { Log.Warn(ex, "GetStat 失败 dev={0}", d.Id); }

                currVal = currVal.HasValue ? Math.Round(currVal.Value, 3) : currVal;
                pressVal = pressVal.HasValue ? Math.Round(pressVal.Value, 3) : pressVal;
                flowVal = flowVal.HasValue ? Math.Round(flowVal.Value, 3) : flowVal;

                int? startup = null;
                var lastStartup = _trendRepo.GetLatest(d.Id, nameof(VarRole.Startup));
                startup = ParseStartup(lastStartup.ValueStr);

                int online = DetermineOnline(d);

                items.Add(new
                {
                    DeviceCode    = d.DeviceCode,
                    DeviceType    = d.DeviceType,
                    Time          = to.ToString("yyyy-MM-dd HH:mm:ss"),
                    Online        = online,
                    Startup       = startup,
                    Currents      = currVal,
                    WaterPressure = pressVal,
                    FlowQuantity  = flowVal
                });
            }

            if (items.Count == 0) return 0;

            string json = JsonConvert.SerializeObject(items);

            try
            {
                _outbox.Enqueue("status", url, json, "variable_trend", null, maxRetry);
                Log.Info("StatusUploadWorker：入队 1 条状态消息，包含 {0} 个设备", items.Count);
                return 1;
            }
            catch (Exception ex) { Log.Warn(ex, "Enqueue status 失败"); return 0; }
        }

        private int DetermineOnline(DeviceEntity d)
        {
            if (_opc != null && !string.IsNullOrEmpty(d.ServerId.ToString()))
            {
                if (!_opc.IsConnected(d.ServerId))
                    return 0;
            }

            if (_trendWriter != null)
            {
                var startupVar = _varRepo.GetByRole(d.Id, nameof(VarRole.Startup));
                if (startupVar != null && !_trendWriter.IsQualityGood(d.Id, nameof(VarRole.Startup)))
                    return 0;
            }

            if (d.DeviceType == "洗车机" && _cam != null)
            {
                var camCfg = new CameraConfigRepository().GetByDevice(d.Id);
                if (camCfg != null && !_cam.IsDeviceOnline(d.Id))
                    return 0;
            }

            return 1;
        }

        /// <summary>把 trend.value_str（"0"/"1"/"true"/"false"）解析成 0/1。</summary>
        private static int ParseStartup(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim();
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (double.TryParse(s, out double d))
                return d != 0 ? 1 : 0;

            return 0;
        }
    }
}
