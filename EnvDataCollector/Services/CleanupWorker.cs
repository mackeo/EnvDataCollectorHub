using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using EnvDataCollector.Data.Repositories;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// 历史数据清理：DB 中三张表（device_snapshot / run_record / push_outbox）的 Success 行
    /// 在 cutoff 之前的全删；plate_event 整表按 created_at 清理（图片识别记录不带推送状态）；
    /// 配套删除 images/{deviceCode}/yyyyMMdd/ 中早于 cutoff 的整目录。
    /// 启动时按 CleanIntervalHours 周期跑；UI 也可调 RunOnce 立即触发。
    /// </summary>
    public sealed class CleanupWorker
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly DeviceSnapshotRepository _snapRepo  = new();
        private readonly RunRecordRepository      _runRepo   = new();
        private readonly OutboxRepository         _outbox    = new();
        private readonly PlateEventRepository     _plateRepo = new();
        private readonly CameraConfigRepository   _camRepo   = new();
        private readonly AppSettingRepository     _settings  = new();

        private System.Threading.Timer _timer;
        private volatile bool _running;

        public bool Running => _timer != null;

        public void Start()
        {
            if (_timer != null) return;
            int hours = _settings.Get<int>(SK.CleanIntervalHours, 24);
            int periodMs = Math.Max(1, hours) * 3600 * 1000;
            // 启动延迟 5 分钟（避免与其它启动任务挤）；之后每 hours 小时
            _timer = new System.Threading.Timer(_ => SafeRun(), null,
                5 * 60 * 1000, periodMs);
            Log.Info("CleanupWorker 已启动，周期 {0} 小时", hours);
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }

        /// <summary>立即触发一次。返回执行结果（用于 UI 显示）。</summary>
        public CleanupResult RunOnce()
        {
            if (_running) return new CleanupResult { Skipped = true };
            _running = true;
            try { return Run(); }
            catch (Exception ex)
            {
                Log.Error(ex, "CleanupWorker.Run 异常");
                return new CleanupResult { Error = ex.Message };
            }
            finally { _running = false; }
        }

        private void SafeRun() { try { Run(); } catch (Exception ex) { Log.Error(ex, "CleanupWorker.SafeRun 异常"); } }

        private CleanupResult Run()
        {
            int days = _settings.Get<int>(SK.CleanRetentionDays, 30);
            if (days < 1) days = 1;
            DateTime cutoff = DateTime.Now.AddDays(-days);
            var r = new CleanupResult { Cutoff = cutoff };

            // SQLite 不返回 affected rows 给 db.Execute（实际上返回，但仓储 API 没透出），
            // 这里只能调一次记录"已尝试"，真实数量由 Log/SQL 反查。简化：不返回精确条数。
            try { _snapRepo.DeleteSuccessOlderThan(cutoff);  r.SnapshotCleaned = true;  } catch (Exception ex) { Log.Warn(ex, "device_snapshot 清理失败"); r.Errors.Add("device_snapshot: " + ex.Message); }
            try { _runRepo.DeleteSuccessOlderThan(cutoff);   r.RunRecordCleaned = true; } catch (Exception ex) { Log.Warn(ex, "run_record 清理失败");      r.Errors.Add("run_record: "      + ex.Message); }
            try { _outbox.DeleteSuccessOlderThan(cutoff);    r.OutboxCleaned = true;    } catch (Exception ex) { Log.Warn(ex, "push_outbox 清理失败");     r.Errors.Add("push_outbox: "     + ex.Message); }
            try { _plateRepo.DeleteOlderThan(cutoff);        r.PlateCleaned = true;     } catch (Exception ex) { Log.Warn(ex, "plate_event 清理失败");     r.Errors.Add("plate_event: "     + ex.Message); }

            r.ImagesDeleted = CleanImageFolders(cutoff, r.Errors);

            Log.Info("Cleanup 完成 cutoff={0:yyyy-MM-dd}，删图 {1} 个目录，errors={2}",
                cutoff, r.ImagesDeleted, r.Errors.Count);
            return r;
        }

        // 图片目录约定：{ImageStorePath}/{deviceCode}/yyyyMMdd/...jpg
        // 清理早于 cutoff 的 yyyyMMdd 整目录。
        private int CleanImageFolders(DateTime cutoff, List<string> errors)
        {
            int deletedDirs = 0;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cutoffStr = cutoff.ToString("yyyyMMdd");

            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var c in _camRepo.GetAll())
                {
                    if (string.IsNullOrWhiteSpace(c.ImageStorePath)) continue;
                    string root = Path.IsPathRooted(c.ImageStorePath)
                        ? c.ImageStorePath
                        : Path.Combine(baseDir, c.ImageStorePath);
                    if (Directory.Exists(root)) roots.Add(root);
                }
            }
            catch (Exception ex) { errors.Add("枚举 camera_config 失败: " + ex.Message); }

            // 兜底默认目录
            string defaultRoot = Path.Combine(baseDir, "images");
            if (Directory.Exists(defaultRoot)) roots.Add(defaultRoot);

            foreach (var root in roots)
            {
                try
                {
                    foreach (var devDir in Directory.EnumerateDirectories(root))
                    {
                        // devDir 是 deviceCode 目录，下面才是 yyyyMMdd
                        foreach (var dayDir in Directory.EnumerateDirectories(devDir))
                        {
                            string name = Path.GetFileName(dayDir);
                            if (name.Length != 8) continue;
                            // 字符串比较即可（yyyyMMdd 词典序 == 时间序）
                            if (string.CompareOrdinal(name, cutoffStr) < 0)
                            {
                                try { Directory.Delete(dayDir, recursive: true); deletedDirs++; }
                                catch (Exception ex) { errors.Add($"删 {dayDir} 失败: {ex.Message}"); }
                            }
                        }
                    }
                }
                catch (Exception ex) { errors.Add($"扫描 {root} 失败: {ex.Message}"); }
            }
            return deletedDirs;
        }

        public sealed class CleanupResult
        {
            public DateTime  Cutoff;
            public bool      SnapshotCleaned;
            public bool      RunRecordCleaned;
            public bool      OutboxCleaned;
            public bool      PlateCleaned;
            public int       ImagesDeleted;
            public bool      Skipped;
            public string    Error;
            public List<string> Errors = new();

            public string Summary()
            {
                if (Skipped) return "上一次清理还在跑，已跳过";
                if (Error != null) return "清理异常：" + Error;
                int tablesOk = (SnapshotCleaned ? 1 : 0) + (RunRecordCleaned ? 1 : 0) +
                               (OutboxCleaned ? 1 : 0) + (PlateCleaned ? 1 : 0);
                string msg = $"截止 {Cutoff:yyyy-MM-dd}：表清理 {tablesOk}/4，删图目录 {ImagesDeleted}";
                if (Errors.Count > 0) msg += $"（{Errors.Count} 个错误）";
                return msg;
            }
        }
    }
}
