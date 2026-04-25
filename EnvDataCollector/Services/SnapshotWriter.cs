using System;
using System.Collections.Generic;
using System.Linq;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// OPC UA 事件 → device_snapshot 表的 producer。
    /// 单变量推送累积进 LiveState（每设备一份），按以下时机各落一行：
    ///  • 边界事件：Startup 0↔1 切换 → 立即 Flush（精确启停时间，给 RunRecordBuilder）
    ///  • 周期触发：Timer 每 PeriodicSec 秒 Flush 当前累积值
    ///  • 会话变化：OnSessionState（连/断）→ 立即 Flush 一次以记录 online 切换
    /// 周期 Flush 与上次 Flush 间隔 &lt; MinPeriodicGapMs 时跳过，避免边界后立刻再写一行。
    /// </summary>
    public sealed class SnapshotWriter
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        // ── 可调参数 ─────────────────────────────────────────
        private const int PeriodicSec      = 10;
        private const int MinPeriodicGapMs = 500;

        private readonly DeviceSnapshotRepository _snapRepo = new();
        private readonly DeviceRepository         _devRepo  = new();

        private readonly object _lock = new();
        private readonly Dictionary<int, LiveState> _state = new();
        private OpcUaService _opc;
        private System.Threading.Timer _timer;
        private Action<int, string, object, DateTime> _onValueHandler;
        private Action<int, bool> _onSessionHandler;

        public bool Running       => _timer != null;
        public int  ActiveDevices { get { lock (_lock) return _state.Count; } }

        public void Start(OpcUaService opc)
        {
            if (_opc != null) return;
            _opc = opc ?? throw new ArgumentNullException(nameof(opc));

            _onValueHandler   = OnValue;
            _onSessionHandler = OnSession;
            _opc.OnValueChanged += _onValueHandler;
            _opc.OnSessionState += _onSessionHandler;

            _timer = new System.Threading.Timer(_ => SafePeriodic(),
                null, PeriodicSec * 1000, PeriodicSec * 1000);
            Log.Info("SnapshotWriter 已启动，周期 {0}s", PeriodicSec);
        }

        public void Stop()
        {
            if (_opc != null)
            {
                try { _opc.OnValueChanged -= _onValueHandler; } catch { }
                try { _opc.OnSessionState -= _onSessionHandler; } catch { }
            }
            try { _timer?.Dispose(); } catch { }
            _timer = null; _opc = null;
            lock (_lock) _state.Clear();
        }

        // ═══════════════════════════════════════════════════════════
        // OPC UA 事件入口
        // ═══════════════════════════════════════════════════════════

        private void OnValue(int deviceId, string role, object value, DateTime ts)
        {
            try
            {
                var s = GetOrAdd(deviceId);
                bool boundary = false;
                lock (s)
                {
                    switch (role)
                    {
                        case nameof(VarRole.Startup):
                            int? newStartup = ToIntOrNull(value);
                            if (newStartup.HasValue && newStartup != s.Startup)
                                boundary = true;
                            s.Startup = newStartup;
                            break;
                        case nameof(VarRole.Currents):
                            s.Currents = ToDoubleOrNull(value);
                            break;
                        case nameof(VarRole.WaterPressure):
                            s.WaterPressure = ToDoubleOrNull(value);
                            break;
                        case nameof(VarRole.FlowQuantity):
                            s.FlowQuantity = ToDoubleOrNull(value);
                            break;
                        // RunStatus / AlarmBit：device_snapshot 当前无字段，忽略
                    }
                }
                if (boundary) Flush(deviceId, ts, "startup-edge", forced: true);
            }
            catch (Exception ex) { Log.Error(ex, "SnapshotWriter.OnValue 异常"); }
        }

        private void OnSession(int serverId, bool connected)
        {
            try
            {
                int onlineVal = connected ? 1 : 0;
                List<int> deviceIds;
                try
                {
                    deviceIds = _devRepo.GetAll(true)
                        .Where(d => d.ServerId == serverId)
                        .Select(d => d.Id).ToList();
                }
                catch (Exception ex) { Log.Warn(ex, "OnSession 查设备失败"); return; }

                foreach (int did in deviceIds)
                {
                    var s = GetOrAdd(did);
                    lock (s) s.Online = onlineVal;
                    Flush(did, DateTime.Now, connected ? "session-up" : "session-down", forced: true);
                }
            }
            catch (Exception ex) { Log.Error(ex, "SnapshotWriter.OnSession 异常"); }
        }

        // ═══════════════════════════════════════════════════════════
        // 周期 Tick
        // ═══════════════════════════════════════════════════════════

        private void SafePeriodic()
        {
            try
            {
                List<int> ids;
                lock (_lock) ids = _state.Keys.ToList();
                foreach (int did in ids)
                    Flush(did, DateTime.Now, "periodic", forced: false);
            }
            catch (Exception ex) { Log.Error(ex, "SnapshotWriter.Periodic 异常"); }
        }

        // ═══════════════════════════════════════════════════════════
        // 落库
        // ═══════════════════════════════════════════════════════════

        private void Flush(int deviceId, DateTime time, string reason, bool forced)
        {
            LiveState s;
            lock (_lock)
            {
                if (!_state.TryGetValue(deviceId, out s)) return;
            }

            DeviceSnapshotEntity ent;
            lock (s)
            {
                if (!forced && (DateTime.Now - s.LastFlush).TotalMilliseconds < MinPeriodicGapMs)
                    return;
                ent = new DeviceSnapshotEntity
                {
                    DeviceId      = deviceId,
                    Time          = time.ToString("yyyy-MM-dd HH:mm:ss"),
                    Online        = s.Online,
                    Startup       = s.Startup,
                    Currents      = s.Currents,
                    WaterPressure = s.WaterPressure,
                    FlowQuantity  = s.FlowQuantity,
                    PushStatus    = "Pending"
                };
                s.LastFlush = DateTime.Now;
            }

            try
            {
                long id = _snapRepo.Insert(ent);
                Log.Debug("snapshot id={0} dev={1} reason={2} startup={3} curr={4} press={5} flow={6}",
                    id, deviceId, reason, ent.Startup, ent.Currents, ent.WaterPressure, ent.FlowQuantity);
            }
            catch (Exception ex) { Log.Warn(ex, "snapshot Insert 失败 dev={0}", deviceId); }
        }

        private LiveState GetOrAdd(int deviceId)
        {
            lock (_lock)
            {
                if (!_state.TryGetValue(deviceId, out var s))
                {
                    s = new LiveState { LastFlush = DateTime.MinValue };
                    _state[deviceId] = s;
                }
                return s;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 类型转换辅助（容错：bool / 各种整数 / 浮点 / 字符串）
        // ═══════════════════════════════════════════════════════════

        private static int? ToIntOrNull(object v)
        {
            if (v == null) return null;
            if (v is bool b) return b ? 1 : 0;
            try { return Convert.ToInt32(v); } catch { }
            // 字符串特殊处理：包含 "." 的浮点数走 ToDouble 再截断
            if (v is string str && double.TryParse(str, out double d)) return (int)d;
            return null;
        }

        private static double? ToDoubleOrNull(object v)
        {
            if (v == null) return null;
            if (v is bool b) return b ? 1.0 : 0.0;
            try { return Convert.ToDouble(v); } catch { return null; }
        }

        private sealed class LiveState
        {
            public int?     Online;
            public int?     Startup;
            public double?  Currents;
            public double?  WaterPressure;
            public double?  FlowQuantity;
            public DateTime LastFlush;
        }
    }
}
