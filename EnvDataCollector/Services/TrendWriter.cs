using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// 监听 OPC UA 变量变化，与上次值不同则缓冲；后台 worker 每 FlushIntervalMs 毫秒
    /// 一次性 INSERT 多条到 variable_trend（同事务多 VALUES，性能比循环单插高十倍以上）。
    /// 缓冲队列用 BlockingCollection，背压：超过 MaxPendingItems 时丢最旧条目并告警。
    /// </summary>
    public sealed class TrendWriter
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        // ── 可调参数 ─────────────────────────────────────────
        private const int FlushIntervalMs  = 1000;   // 批量 flush 周期
        private const int MaxFlushBatch    = 500;    // 单次 flush 上限（防一次写太多锁库）
        private const int MaxPendingItems  = 50000;  // 缓冲上限（防内存爆）

        private readonly VariableTrendRepository  _trendRepo = new();
        private readonly DeviceVariableRepository _varRepo   = new();

        private readonly object _lastLock = new();
        private readonly Dictionary<(int, string), string> _lastValues = new();
        private readonly ConcurrentDictionary<(int, string), VarInfo> _varInfoCache = new();

        private readonly ConcurrentQueue<VariableTrendEntity> _queue = new();
        private long _droppedCount;       // 因背压丢弃的条目数（统计用）

        private OpcUaService _opc;
        private Action<int, string, object, DateTime> _onValueHandler;
        private System.Threading.Timer _flushTimer;
        private volatile bool _flushing;

        public bool Running => _opc != null;
        public int  PendingCount => _queue.Count;
        public long DroppedCount => Interlocked.Read(ref _droppedCount);

        public void Start(OpcUaService opc)
        {
            if (_opc != null) return;
            _opc = opc ?? throw new ArgumentNullException(nameof(opc));

            _onValueHandler = OnValue;
            _opc.OnValueChanged += _onValueHandler;

            _flushTimer = new System.Threading.Timer(_ => SafeFlush(), null,
                FlushIntervalMs, FlushIntervalMs);

            Log.Info("TrendWriter 已启动（批量 flush 周期 {0}ms，单批上限 {1}）",
                FlushIntervalMs, MaxFlushBatch);
        }

        public void Stop()
        {
            if (_opc != null)
            {
                try { _opc.OnValueChanged -= _onValueHandler; } catch { }
            }
            try { _flushTimer?.Dispose(); } catch { }
            _flushTimer = null;
            _opc = null;

            // 收尾：把残留缓冲冲完
            SafeFlush();

            lock (_lastLock) _lastValues.Clear();
            _varInfoCache.Clear();
        }

        /// <summary>外部强制 flush（测试/退出时用）。返回写入条数。</summary>
        public int FlushNow() => DoFlush();

        // ═══════════════════════════════════════════════════════════
        // OPC UA 事件入口（仅做"是否变化"判断 + 入队，IO 全部丢给 worker）
        // ═══════════════════════════════════════════════════════════

        private void OnValue(int deviceId, string role, object value, DateTime ts)
        {
            try
            {
                string valueStr = value?.ToString() ?? "";
                var key = (deviceId, role);

                lock (_lastLock)
                {
                    if (_lastValues.TryGetValue(key, out string last) && last == valueStr) return;
                    _lastValues[key] = valueStr;
                }

                // 背压：缓冲爆了，丢老的
                if (_queue.Count >= MaxPendingItems)
                {
                    _queue.TryDequeue(out _);
                    if (Interlocked.Increment(ref _droppedCount) % 100 == 1)
                        Log.Warn("TrendWriter 缓冲超限（{0} 条），开始丢弃最旧条目；累计丢弃 {1}",
                            MaxPendingItems, _droppedCount);
                }

                var info = GetOrAddVarInfo(deviceId, role);
                _queue.Enqueue(new VariableTrendEntity
                {
                    DeviceId   = deviceId,
                    VariableId = info?.Id ?? 0,
                    NodeId     = info?.NodeId ?? role,
                    VarRole    = role,
                    ValueStr   = valueStr,
                    SourceTime = ts.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
            catch (Exception ex) { Log.Error(ex, "TrendWriter.OnValue 异常"); }
        }

        // ═══════════════════════════════════════════════════════════
        // 批量 flush
        // ═══════════════════════════════════════════════════════════

        private void SafeFlush() { try { DoFlush(); } catch (Exception ex) { Log.Error(ex, "TrendWriter.Flush 异常"); } }

        private int DoFlush()
        {
            if (_flushing) return 0;
            _flushing = true;
            try
            {
                int total = 0;
                while (_queue.Count > 0)
                {
                    var batch = new List<VariableTrendEntity>(Math.Min(MaxFlushBatch, _queue.Count));
                    while (batch.Count < MaxFlushBatch && _queue.TryDequeue(out var item))
                        batch.Add(item);
                    if (batch.Count == 0) break;

                    try
                    {
                        int n = _trendRepo.InsertBatch(batch);
                        total += n;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "TrendWriter 批量写入失败，丢弃本批 {0} 条", batch.Count);
                    }
                }
                if (total > 0) Log.Debug("TrendWriter flush {0} 条", total);
                return total;
            }
            finally { _flushing = false; }
        }

        // ═══════════════════════════════════════════════════════════
        // 工具
        // ═══════════════════════════════════════════════════════════

        private DeviceVariableEntity GetOrAddVarInfo(int deviceId, string varRole)
        {
            var key = (deviceId, varRole);
            if (_varInfoCache.TryGetValue(key, out var cached))
                return new DeviceVariableEntity { Id = cached.Id, NodeId = cached.NodeId };

            try
            {
                var entity = _varRepo.GetByRole(deviceId, varRole);
                if (entity != null)
                {
                    _varInfoCache[key] = new VarInfo { Id = entity.Id, NodeId = entity.NodeId };
                    return entity;
                }
            }
            catch (Exception ex) { Log.Warn(ex, "TrendWriter 查询变量信息失败 dev={0} role={1}", deviceId, varRole); }

            return null;
        }

        private sealed class VarInfo
        {
            public int Id;
            public string NodeId;
        }
    }
}
