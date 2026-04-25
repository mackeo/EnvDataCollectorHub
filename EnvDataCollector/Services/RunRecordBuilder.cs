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
    /// 仅依赖 device_snapshot.startup 字段：0/null=空闲，1=运行；遇到 0→1 边界开记录，
    /// 1→0 边界关记录并写入 run_record，关闭时调 PlateEventRepository.FindBestMatch 绑车牌。
    /// 用 AppSetting 存 lastId 游标，重启不丢不重；用 GetLastBefore 恢复"进行中"状态。
    /// 不绑定 OPC UA 实时事件 —— 等 device_snapshot 落库做完后无缝接上。
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
        private const string LastIdKey    = "RunRecord.LastScannedSnapshotId";

        private readonly DeviceSnapshotRepository _snapRepo  = new();
        private readonly RunRecordRepository      _runRepo   = new();
        private readonly DeviceRepository         _devRepo   = new();
        private readonly PlateEventRepository     _plateRepo = new();
        private readonly CameraConfigRepository   _camRepo   = new();
        private readonly AppSettingRepository     _settings  = new();

        private readonly object _lock = new();
        private readonly Dictionary<int, OpenRecord> _open = new();   // device_id -> 进行中
        private long _lastId;
        private System.Threading.Timer _timer;
        private volatile bool _scanning;

        public bool Running => _timer != null;

        public void Start()
        {
            lock (_lock)
            {
                if (_timer != null) return;
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

            // 1) 取增量快照，按 (device_id, time, id) 聚合处理（避免不同设备的快照交错）
            var batch = _snapRepo.QueryAfter(_lastId, BatchSize).ToList();
            if (batch.Count == 0)
            {
                CheckStale(ref closedCount);
                return closedCount;
            }

            var grouped = batch
                .GroupBy(s => s.DeviceId)
                .Select(g => new { DeviceId = g.Key, Items = g.OrderBy(x => x.Time).ThenBy(x => x.Id).ToList() });

            foreach (var dev in grouped)
            {
                // 仅处理"洗车机"类型
                var d = _devRepo.GetById(dev.DeviceId);
                if (d == null || d.DeviceType != "洗车机")
                {
                    foreach (var s in dev.Items) if (s.Id > maxIdSeen) maxIdSeen = s.Id;
                    continue;
                }

                foreach (var s in dev.Items)
                {
                    bool isRunning = (s.Startup ?? 0) == 1;
                    bool hasOpen   = _open.TryGetValue(d.Id, out var open);

                    if (!hasOpen && isRunning)
                    {
                        _open[d.Id] = new OpenRecord
                        {
                            Device    = d,
                            StartTime = ParseTime(s.Time),
                            Buffer    = new List<DeviceSnapshotEntity> { s }
                        };
                    }
                    else if (hasOpen && isRunning)
                    {
                        open.Buffer.Add(s);
                    }
                    else if (hasOpen && !isRunning)
                    {
                        if (CloseRecord(open, ParseTime(s.Time), "正常停止")) closedCount++;
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
                Log.Info("RunRecordBuilder 本轮关闭 {0} 条记录，lastId={1}", closedCount, _lastId);
            return closedCount;
        }

        /// <summary>对每个 OpenRecord，若最后一条 snapshot 距今 > StaleSec 强制关闭。</summary>
        private void CheckStale(ref int closedCount)
        {
            if (_open.Count == 0) return;
            var now = DateTime.Now;
            foreach (var kv in _open.ToList())
            {
                var open = kv.Value;
                var lastSnapTime = ParseTime(open.Buffer[open.Buffer.Count - 1].Time);
                if ((now - lastSnapTime).TotalSeconds > StaleSec)
                {
                    if (CloseRecord(open, lastSnapTime, "数据中断")) closedCount++;
                    _open.Remove(kv.Key);
                }
            }
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

            // 累积窗口数据：内存 buffer + 补齐 DB 中可能漏的窗口（双保险，用于跨扫描周期的长记录）
            var snapsAll = new List<DeviceSnapshotEntity>(open.Buffer);
            try
            {
                var ext = _snapRepo.QueryRange(open.Device.Id, open.StartTime, endTime).ToList();
                if (ext.Count > snapsAll.Count) snapsAll = ext;   // DB 视图更全则用 DB
            }
            catch (Exception ex) { Log.Debug(ex, "QueryRange 失败，使用内存 buffer"); }

            var currents = snapsAll.Where(s => s.Currents.HasValue).Select(s => s.Currents.Value).ToList();
            var press    = snapsAll.Where(s => s.WaterPressure.HasValue).Select(s => s.WaterPressure.Value).ToList();
            var flow     = snapsAll.Where(s => s.FlowQuantity.HasValue).Select(s => s.FlowQuantity.Value).ToList();
            var lastSnap = snapsAll[snapsAll.Count - 1];

            var rec = new RunRecordEntity
            {
                DeviceId   = open.Device.Id,
                DeviceType = open.Device.DeviceType,
                DeviceCode = open.Device.DeviceCode,
                StartTime  = open.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                EndTime    = endTime.ToString("yyyy-MM-dd HH:mm:ss"),
                RunTimeSec = runSec,

                Currents      = lastSnap.Currents,
                WaterPressure = lastSnap.WaterPressure,
                FlowQuantity  = lastSnap.FlowQuantity,

                CurrentsMax    = currents.Count > 0 ? currents.Max() : (double?)null,
                CurrentsMin    = currents.Count > 0 ? currents.Min() : (double?)null,
                CurrentsMedian = Median(currents),

                WaterPressureMax    = press.Count > 0 ? press.Max() : (double?)null,
                WaterPressureMin    = press.Count > 0 ? press.Min() : (double?)null,
                WaterPressureMedian = Median(press),

                FlowQuantityMax    = flow.Count > 0 ? flow.Max() : (double?)null,
                FlowQuantityMin    = flow.Count > 0 ? flow.Min() : (double?)null,
                FlowQuantityMedian = Median(flow),

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
                }
            }
            catch (Exception ex) { Log.Warn(ex, "FindBestMatch 异常"); }

            try
            {
                long id = _runRepo.Insert(rec);
                Log.Info("RunRecord 入库 id={0}，{1} 时长 {2}s，车牌 {3}",
                    id, open.Device.DeviceCode, runSec, rec.VehicleNo ?? "(未匹配)");
            }
            catch (Exception ex) { Log.Error(ex, "RunRecord Insert 失败"); return false; }
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
                var last = _snapRepo.GetLastBefore(d.Id, DateTime.Now);
                if (last == null) continue;
                if ((last.Startup ?? 0) != 1) continue;

                _open[d.Id] = new OpenRecord
                {
                    Device    = d,
                    StartTime = ParseTime(last.Time),
                    Buffer    = new List<DeviceSnapshotEntity> { last }
                };
                Log.Info("Recover：设备 {0} 处于进行中状态（自 {1} 起）",
                    d.DeviceCode, last.Time);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 工具
        // ═══════════════════════════════════════════════════════════

        private static double? Median(List<double> xs)
        {
            if (xs == null || xs.Count == 0) return null;
            var sorted = xs.OrderBy(x => x).ToList();
            int n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }

        private static DateTime ParseTime(string s) =>
            DateTime.TryParse(s, out var t) ? t : DateTime.Now;

        private static long ParseLong(string s, long def) =>
            long.TryParse(s, out var v) ? v : def;

        private sealed class OpenRecord
        {
            public DeviceEntity Device;
            public DateTime StartTime;
            public List<DeviceSnapshotEntity> Buffer;
        }
    }
}
