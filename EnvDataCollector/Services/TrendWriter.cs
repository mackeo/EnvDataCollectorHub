using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using NLog;

namespace EnvDataCollector.Services
{
    public sealed class TrendWriter
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly VariableTrendRepository _trendRepo = new();
        private readonly DeviceVariableRepository _varRepo = new();

        private readonly object _lock = new();
        private readonly Dictionary<(int, string), string> _lastValues = new();
        private readonly ConcurrentDictionary<(int, string), VarInfo> _varInfoCache = new();

        private OpcUaService _opc;
        private Action<int, string, object, DateTime> _onValueHandler;

        public bool Running => _opc != null;

        public void Start(OpcUaService opc)
        {
            if (_opc != null) return;
            _opc = opc ?? throw new ArgumentNullException(nameof(opc));

            _onValueHandler = OnValue;
            _opc.OnValueChanged += _onValueHandler;

            Log.Info("TrendWriter 已启动，监听变量变化落库");
        }

        public void Stop()
        {
            if (_opc != null)
            {
                try { _opc.OnValueChanged -= _onValueHandler; } catch { }
            }
            _opc = null;
            lock (_lock) _lastValues.Clear();
            _varInfoCache.Clear();
        }

        private void OnValue(int deviceId, string role, object value, DateTime ts)
        {
            try
            {
                string valueStr = value?.ToString() ?? "";
                var key = (deviceId, role);

                bool changed;
                lock (_lock)
                {
                    if (_lastValues.TryGetValue(key, out string last) && last == valueStr)
                        return;
                    _lastValues[key] = valueStr;
                    changed = true;
                }

                if (!changed) return;

                var info = GetOrAddVarInfo(deviceId, role);

                var ent = new VariableTrendEntity
                {
                    DeviceId = deviceId,
                    VariableId = info?.Id ?? 0,
                    NodeId = info?.NodeId ?? role,
                    VarRole = role,
                    ValueStr = valueStr,
                    SourceTime = ts.ToString("yyyy-MM-dd HH:mm:ss"),
                };

                long id = _trendRepo.Insert(ent);
                Log.Debug("trend id={0} dev={1} role={2} val={3} ts={4}",
                    id, deviceId, role, valueStr, ent.SourceTime);
            }
            catch (Exception ex) { Log.Error(ex, "TrendWriter.OnValue 异常"); }
        }

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
