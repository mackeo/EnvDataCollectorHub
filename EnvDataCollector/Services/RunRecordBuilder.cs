using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using Dapper;
using EnvDataCollector.Data;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// 洗车机运行记录拼接服务（消费方）。
    /// 仅依赖 variable_trend 中 var_role=Startup 的事件：value_str "0"/"1"，
    /// 0→1 开记录，1→0 关记录并写 run_record，关闭时调 PlateEventRepository.FindBestMatch 绑车牌。
    /// 用 AppSetting 存 trend.id 游标，重启不丢不重；用 GetLastStartup 恢复"进行中"状态。
    /// </summary>
    public sealed class RunRecordBuilder
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        // ── 可调参数 ─────────────────────────────────────────
        private const int ScanIntervalSec = 5;
        private const int MinRunSec       = 3;
        private const int MaxRunSec       = 14400;   // 4h
        private const int StaleSec        = 600;     // 10min 数据中断阈值
        private const int BatchSize       = 1000;
        private const string LastIdKey    = "RunRecord.LastTrendId";

        private readonly DeviceSnapshotRepository _snapRepo  = new();   // 仍持有但不再读，留作兼容字段
        private readonly VariableTrendRepository  _trendRepo = new();
        private readonly RunRecordRepository      _runRepo   = new();
        private readonly DeviceRepository         _devRepo   = new();
        private readonly PlateEventRepository     _plateRepo = new();
        private readonly CameraConfigRepository   _camRepo   = new();
        private readonly OutboxRepository         _outboxRepo = new();
        private readonly AppSettingRepository     _settings  = new();

        private ImageUploadService _imageUploader;

        private readonly object _lock = new();
        private readonly Dictionary<int, OpenRecord> _open = new();   // device_id -> 进行中
        private long _lastId;
        private System.Threading.Timer _timer;
        private volatile bool _scanning;

        public bool Running => _timer != null;

        public void Start(ImageUploadService imageUploader = null)
        {
            lock (_lock)
            {
                if (_timer != null) return;
                _imageUploader = imageUploader;
                _lastId = ParseLong(_settings.Get(LastIdKey, "0"), 0);
                Recover();
                _timer = new System.Threading.Timer(_ => SafeScan(),
                    null, 1000, ScanIntervalSec * 1000);
                Log.Info("RunRecordBuilder 已启动，lastId={0}，进行中 {1} 台", _lastId, _open.Count);
            }
        }

        public void Stop()
        {
            System.Threading.Timer t;
            lock (_lock) { t = _timer; _timer = null; }
            try { t?.Dispose(); } catch { }
            // 不持久化 _open（OpenRecord 是内存状态，重启用 Recover 重建）
        }

        /// <summary>手动触发一次扫描，返回新生成的 RunRecord 数。</summary>
        public int ScanOnce()
        {
            return SafeScan();
        }

        /// <summary>对已存在的 RunRecord 重新跑车牌匹配（用其 StartTime 作为 baseTime）。</summary>
        public bool RematchPlate(long runRecordId)
        {
            var rec = _runRepo.GetById(runRecordId);
            if (rec == null) return false;
            if (!DateTime.TryParse(rec.StartTime, out var baseTime)) return false;

            int pre = 30, post = 120;
            var cam = _camRepo.GetByDevice(rec.DeviceId);
            if (cam != null) { pre = cam.MatchPreSec; post = cam.MatchPostSec; }

            var plate = _plateRepo.FindBestMatch(rec.DeviceId, baseTime, pre, post);
            if (plate == null) return false;

            // 直接 SQL 更新（仓储无现成方法）
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"
                UPDATE run_record
                SET vehicle_no=@v,
                    vehicle_pic_local=@vp, vehicle_no_pic_local=@np,
                    vehicle_pic=@vu, vehicle_no_pic=@nu
                WHERE id=@id",
                new {
                    id = runRecordId,
                    v  = plate.PlateNo,
                    vp = plate.VehiclePicLocal, np = plate.PlatePicLocal,
                    vu = plate.VehiclePicUrl,   nu = plate.PlatePicUrl
                });
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        // 核心扫描
        // ═══════════════════════════════════════════════════════════

        private int SafeScan()
        {
            if (_scanning) return 0;
            _scanning = true;
            try { return Scan(); }
            catch (Exception ex) { Log.Error(ex, "RunRecordBuilder.Scan 异常"); return 0; }
            finally { _scanning = false; }
        }

        private int Scan()
        {
            int closedCount = 0;
            long maxIdSeen = _lastId;

            // 1) 增量取 var_role=Startup 的事件，按 id 升序（id 升序就代表事件发生顺序）
            var batch = _trendRepo.QueryStartupAfter(_lastId, BatchSize).ToList();
            if (batch.Count == 0)
            {
                CheckStale(ref closedCount);
                return closedCount;
            }

            // 2) 按设备分组，组内按 id 升序处理（同设备里 id 单调代表事件先后）
            var grouped = batch
                .GroupBy(s => s.DeviceId)
                .Select(g => new { DeviceId = g.Key, Items = g.OrderBy(x => x.Id).ToList() });

            foreach (var dev in grouped)
            {
                var d = _devRepo.GetById(dev.DeviceId);
                if (d == null || d.DeviceType != "洗车机")
                {
                    foreach (var s in dev.Items) if (s.Id > maxIdSeen) maxIdSeen = s.Id;
                    continue;
                }

                foreach (var s in dev.Items)
                {
                    bool isRunning = ParseStartup(s.ValueStr) == 1;
                    DateTime ts    = ParseTime(s.SourceTime);
                    bool hasOpen   = _open.TryGetValue(d.Id, out var open);

                    if (!hasOpen && isRunning)
                    {
                        _open[d.Id] = new OpenRecord { Device = d, StartTime = ts, LastSeen = ts };
                    }
                    else if (hasOpen && isRunning)
                    {
                        open.LastSeen = ts;   // 重复 1，刷新心跳
                    }
                    else if (hasOpen && !isRunning)
                    {
                        if (CloseRecord(open, ts, "正常停止")) closedCount++;
                        _open.Remove(d.Id);
                    }
                    // 无 + 0：忽略

                    if (s.Id > maxIdSeen) maxIdSeen = s.Id;
                }
            }

            if (maxIdSeen != _lastId)
            {
                _lastId = maxIdSeen;
                _settings.Set(LastIdKey, _lastId.ToString());
            }

            CheckStale(ref closedCount);

            if (closedCount > 0)
                Log.Info("RunRecordBuilder 本轮关闭 {0} 条记录，lastTrendId={1}", closedCount, _lastId);
            return closedCount;
        }

        /// <summary>对每个 OpenRecord，若 LastSeen 距今 > StaleSec 强制关闭。</summary>
        private void CheckStale(ref int closedCount)
        {
            if (_open.Count == 0) return;
            var now = DateTime.Now;
            foreach (var kv in _open.ToList())
            {
                var open = kv.Value;
                if ((now - open.LastSeen).TotalSeconds > StaleSec)
                {
                    if (CloseRecord(open, open.LastSeen, "数据中断")) closedCount++;
                    _open.Remove(kv.Key);
                }
            }
        }

        /// <summary>把 trend.value_str（"0"/"1"/"true"/"false"）解析成 0/1。</summary>
        private static int ParseStartup(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim();
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1;
            if (double.TryParse(s, out double d)) return d != 0 ? 1 : 0;
            return 0;
        }

        // ═══════════════════════════════════════════════════════════
        // 关闭一条进行中的记录：算统计 + 车牌匹配 + 写库
        // ═══════════════════════════════════════════════════════════

        private bool CloseRecord(OpenRecord open, DateTime endTime, string reason)
        {
            int runSec = (int)Math.Round((endTime - open.StartTime).TotalSeconds);
            if (runSec < MinRunSec)
            {
                Log.Debug("丢弃毛刺：device={0}，start={1}，end={2}，sec={3}",
                    open.Device.DeviceCode, open.StartTime, endTime, runSec);
                return false;
            }
            if (runSec > MaxRunSec)
            {
                reason = "超时强制关闭";
                runSec = MaxRunSec;
            }

            // 从 variable_trend SQL 聚合获取运行窗口内变量统计
            VariableTrendStats currStats, pressStats, flowStats;
            try
            {
                currStats = _trendRepo.GetStats(open.Device.Id, nameof(VarRole.Currents),      open.StartTime, endTime);
                pressStats = _trendRepo.GetStats(open.Device.Id, nameof(VarRole.WaterPressure), open.StartTime, endTime);
                flowStats  = _trendRepo.GetStats(open.Device.Id, nameof(VarRole.FlowQuantity),  open.StartTime, endTime);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "variable_trend GetStats 失败，统计数据为空");
                currStats = new VariableTrendStats();
                pressStats = new VariableTrendStats();
                flowStats  = new VariableTrendStats();
            }

            var rec = new RunRecordEntity
            {
                DeviceId   = open.Device.Id,
                DeviceType = open.Device.DeviceType,
                DeviceCode = open.Device.DeviceCode,
                StartTime  = open.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                EndTime    = endTime.ToString("yyyy-MM-dd HH:mm:ss"),
                RunTimeSec = runSec,

                Currents      = currStats.Last,
                WaterPressure = pressStats.Last,
                FlowQuantity  = flowStats.Last,

                CurrentsMax      = currStats.Max,
                CurrentsMin      = currStats.Min,
                CurrentsMedian   = currStats.Median,

                WaterPressureMax    = pressStats.Max,
                WaterPressureMin    = pressStats.Min,
                WaterPressureMedian = pressStats.Median,

                FlowQuantityMax    = flowStats.Max,
                FlowQuantityMin    = flowStats.Min,
                FlowQuantityMedian = flowStats.Median,

                CloseReason = reason,
                PushStatus  = "Pending"
            };

            // 车牌匹配（用 StartTime 作 baseTime）
            int pre = 30, post = 120;
            var cam = _camRepo.GetByDevice(open.Device.Id);
            if (cam != null) { pre = cam.MatchPreSec; post = cam.MatchPostSec; }
            try
            {
                var plate = _plateRepo.FindBestMatch(open.Device.Id, open.StartTime, pre, post);
                if (plate != null)
                {
                    rec.VehicleNo          = plate.PlateNo;
                    rec.VehiclePicLocal    = plate.VehiclePicLocal;
                    rec.VehicleNoPicLocal  = plate.PlatePicLocal;
                    rec.VehiclePic         = plate.VehiclePicUrl;
                    rec.VehicleNoPic       = plate.PlatePicUrl;

                    if (_imageUploader != null)
                    {
                        if (string.IsNullOrEmpty(rec.VehiclePic) && !string.IsNullOrEmpty(plate.VehiclePicLocal))
                        {
                            string remote = _imageUploader.UploadFromLocal(plate.VehiclePicLocal);
                            if (!string.IsNullOrEmpty(remote))
                            {
                                rec.VehiclePic = remote;
                                try { _plateRepo.UpdateRemoteUrls(plate.Id, remote, plate.PlatePicUrl); }
                                catch (Exception ex) { Log.Debug(ex, "回填 plate_event vehicle URL 失败"); }
                            }
                        }
                        if (string.IsNullOrEmpty(rec.VehicleNoPic) && !string.IsNullOrEmpty(plate.PlatePicLocal))
                        {
                            string remote = _imageUploader.UploadFromLocal(plate.PlatePicLocal);
                            if (!string.IsNullOrEmpty(remote))
                            {
                                rec.VehicleNoPic = remote;
                                try { _plateRepo.UpdateRemoteUrls(plate.Id, rec.VehiclePic, remote); }
                                catch (Exception ex) { Log.Debug(ex, "回填 plate_event plate URL 失败"); }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn(ex, "FindBestMatch 异常"); }

            long newId;
            try
            {
                newId = _runRepo.Insert(rec);
                Log.Info("RunRecord 入库 id={0}，{1} 时长 {2}s，车牌 {3}",
                    newId, open.Device.DeviceCode, runSec, rec.VehicleNo ?? "(未匹配)");
            }
            catch (Exception ex) { Log.Error(ex, "RunRecord Insert 失败"); return false; }

            // 事件上报入队（EventApiUrl 未配则跳过，不 spam）
            try
            {
                string eventUrl = _settings.Get(SK.EventApiUrl);
                if (!string.IsNullOrWhiteSpace(eventUrl))
                {
                    int maxRetry = _settings.Get<int>(SK.MaxRetryCount, 10);
                    rec.Id = newId;
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(rec);
                    _outboxRepo.Enqueue("event", eventUrl, json, "run_record", newId, maxRetry);
                }
            }
            catch (Exception ex) { Log.Warn(ex, "RunRecord 入队 push_outbox 失败 id={0}", newId); }
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        // 启动时恢复进行中状态
        // ═══════════════════════════════════════════════════════════

        private void Recover()
        {
            _open.Clear();
            foreach (var d in _devRepo.GetAll(true).Where(x => x.DeviceType == "洗车机"))
            {
                var last = _trendRepo.GetLastStartup(d.Id);
                if (last == null) continue;
                if (ParseStartup(last.ValueStr) != 1) continue;

                var ts = ParseTime(last.SourceTime);
                _open[d.Id] = new OpenRecord
                {
                    Device    = d,
                    StartTime = ts,
                    LastSeen  = ts
                };
                Log.Info("Recover：设备 {0} 处于进行中状态（自 {1} 起）",
                    d.DeviceCode, last.SourceTime);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 工具
        // ═══════════════════════════════════════════════════════════

        private static DateTime ParseTime(string s) =>
            DateTime.TryParse(s, out var t) ? t : DateTime.Now;

        private static long ParseLong(string s, long def) =>
            long.TryParse(s, out var v) ? v : def;

        private sealed class OpenRecord
        {
            public DeviceEntity Device;
            public DateTime StartTime;
            public DateTime LastSeen;
        }
    }
}
