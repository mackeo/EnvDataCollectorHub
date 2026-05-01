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
            int maxRetry = _settings.Get<int>(SK.MaxRetryCount, 10);

            var devs = _devRepo.GetAll(true)
                .Where(d => d.DeviceType == "洗车机" || d.DeviceType == "雾炮" || d.DeviceType == "干雾")
                .ToList();
            if (devs.Count == 0) return 0;

            DateTime to = DateTime.Now;
            DateTime from = to.AddSeconds(-interval);
            int enqueued = 0;

            foreach (var d in devs)
            {
                double? currVal, pressVal, flowVal;
                try
                {
                    currVal  = _trendRepo.GetStat(d.Id, nameof(VarRole.Currents),      from, to, mode);
                    pressVal = _trendRepo.GetStat(d.Id, nameof(VarRole.WaterPressure), from, to, "Max");
                    flowVal  = _trendRepo.GetStat(d.Id, nameof(VarRole.FlowQuantity),  from, to, mode);
                }
                catch (Exception ex) { Log.Warn(ex, "GetStat 失败 dev={0}", d.Id); continue; }

                currVal = currVal.HasValue ? Math.Round(currVal.Value, 3) : currVal;
                pressVal = pressVal.HasValue ? Math.Round(pressVal.Value, 3) : pressVal;
                flowVal = flowVal.HasValue ? Math.Round(flowVal.Value, 3) : flowVal;

                int? startup = null;
                var lastStartup = _trendRepo.GetLatest(d.Id, nameof(VarRole.Startup));
                if (lastStartup != null && int.TryParse(lastStartup.ValueStr, out int sv))
                    startup = sv;

                if (currVal == null && pressVal == null && flowVal == null && startup == null) continue;

                int online = DetermineOnline(d);

                var payload = new
                {
                    DeviceCode    = d.DeviceCode,
                    DeviceType    = d.DeviceType,
                    Time          = to.ToString("yyyy-MM-dd HH:mm:ss"),
                    Online        = online,
                    Startup = startup,
                    Currents    = currVal,
                    WaterPressure = pressVal,
                    FlowQuantity  = flowVal
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
    }
}
