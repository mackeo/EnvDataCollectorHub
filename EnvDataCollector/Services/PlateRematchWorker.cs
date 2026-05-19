using System;
using System.Linq;
using System.Threading;
using EnvDataCollector.Data.Repositories;
using NLog;

namespace EnvDataCollector.Services
{
    public sealed class PlateRematchWorker
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const int IntervalSec = 300;

        private readonly RunRecordRepository _runRepo = new();
        private readonly AppSettingRepository _settings = new();

        private RunRecordBuilder _builder;
        private System.Threading.Timer _timer;
        private volatile bool _running;

        public bool Running => _timer != null;

        public void Start(RunRecordBuilder builder)
        {
            if (_timer != null) return;
            _builder = builder;
            _timer = new System.Threading.Timer(_ => SafeRun(), null,
                IntervalSec * 1000, IntervalSec * 1000);
            Log.Info("PlateRematchWorker 已启动，间隔 {0}s", IntervalSec);
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }

        public int RunOnce()
        {
            if (_running) return 0;
            _running = true;
            try { return Run(); }
            catch (Exception ex)
            {
                Log.Error(ex, "PlateRematchWorker.Run 异常");
                return 0;
            }
            finally { _running = false; }
        }

        private void SafeRun()
        {
            try { Run(); }
            catch (Exception ex) { Log.Error(ex, "PlateRematchWorker.SafeRun 异常"); }
        }

        private int Run()
        {
            if (_builder == null) return 0;

            var records = _runRepo.QueryByCloseReason("车牌识别异常").ToList();
            if (records.Count == 0) return 0;

            Log.Info("PlateRematchWorker 查到 {0} 条车牌识别异常记录，开始重匹配", records.Count);

            int ok = 0, miss = 0;
            foreach (var rec in records)
            {
                try
                {
                    if (_builder.RematchPlate(rec.Id)) ok++;
                    else miss++;
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "PlateRematchWorker RematchPlate id={0} 异常", rec.Id);
                    miss++;
                }
            }

            Log.Info("PlateRematchWorker 本轮完成：匹配成功 {0} 条，未匹配 {1} 条", ok, miss);
            return ok;
        }
    }
}
