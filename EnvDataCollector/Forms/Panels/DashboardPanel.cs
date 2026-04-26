using System;
using System.Configuration;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using NLog;

namespace EnvDataCollector.Forms.Panels
{
    public class DashboardPanel : PanelBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly MainForm _main;
        private readonly OutboxRepository       _outbox = new();
        private readonly CameraConfigRepository _camRepo = new();

        private Label _lblOpc, _lblCam, _lblFailed, _lblPending, _lblOldest;
        private readonly System.Windows.Forms.Timer _timer;

        public DashboardPanel(MainForm main)
        {
            _main = main;
            BuildUI();
            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += (s, e) => UpdateCards();
            _timer.Start();
        }

        private void BuildUI()
        {
            var cards = new FlowLayoutPanel
                { Dock = DockStyle.Top, Height = 112, Padding = new Padding(8, 8, 8, 0) };
            _lblOpc     = UIHelper.MakeCard("OPC UA 断线",  "0", UIHelper.C.Info);
            _lblCam     = UIHelper.MakeCard("摄像头离线",   "-", UIHelper.C.Purple);
            _lblFailed  = UIHelper.MakeCard("推送失败",     "-", UIHelper.C.Danger);
            _lblPending = UIHelper.MakeCard("待推送",       "-", UIHelper.C.Warning);
            _lblOldest  = UIHelper.MakeCard("最久积压(分)", "-", UIHelper.C.Success);
            cards.Controls.AddRange(new Control[]
                { _lblOpc, _lblCam, _lblFailed, _lblPending, _lblOldest });

            var btnRetry = UIHelper.MakeBtn("⚡ 立即触发补传", UIHelper.C.Success);
            var btnDiag  = UIHelper.MakeBtn("📦 导出诊断包",   UIHelper.C.Purple);
            btnRetry.Click += (s, e) => TriggerRetry();
            btnDiag.Click  += (s, e) => ExportDiagnostic();
            var toolbar = UIHelper.MakeToolbar(btnRetry, btnDiag);

            var grid = UIHelper.MakeGrid();
            grid.Columns.AddRange(
                UIHelper.Col("Time",  "时间",   145),
                UIHelper.Col("Level", "级别",    55),
                UIHelper.Col("Msg",   "告警信息", 0, true));

            Controls.Add(grid);
            Controls.Add(UIHelper.MakeSeparator());
            Controls.Add(cards);
            Controls.Add(toolbar);
        }

        private void UpdateCards()
        {
            UIHelper.SafeInvoke(this, () =>
            {
                try
                {
                    int opcDisc = _main.Opc.DisconnectedCount();
                    _lblOpc.Text = $"OPC UA 断线\n{opcDisc}";

                    int camTotal = 0;
                    try { camTotal = _camRepo.GetAll(enabledOnly: true).Count(); } catch { }
                    int camActive = _main.Cam?.ActiveCount ?? 0;
                    int camOff = Math.Max(0, camTotal - camActive);
                    _lblCam.Text = $"摄像头离线\n{camOff}/{camTotal}";

                    var (failed, pending) = _outbox.GetCounts();
                    _lblFailed.Text  = $"推送失败\n{failed}";
                    _lblPending.Text = $"待推送\n{pending}";

                    var oldest = _outbox.GetOldestPendingTime();
                    if (oldest.HasValue)
                    {
                        var minutes = (int)(DateTime.Now - oldest.Value).TotalMinutes;
                        _lblOldest.Text = $"最久积压(分)\n{minutes}";
                    }
                    else
                    {
                        _lblOldest.Text = "最久积压(分)\n-";
                    }
                }
                catch (Exception ex) { Log.Debug(ex, "UpdateCards 异常"); }
            });
        }

        public override void RefreshData() => UpdateCards();

        // ══════════════════════════════════════════════════════
        // 立即触发补传
        // ══════════════════════════════════════════════════════

        private void TriggerRetry()
        {
            if (_main?.Pusher == null) { Tip("PushWorker 未初始化"); return; }
            try
            {
                int ok = _main.Pusher.RunOnce();
                MessageBox.Show($"已触发推送，本轮成功 {ok} 条。",
                    "立即触发补传", MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateCards();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TriggerRetry 失败");
                MessageBox.Show("触发失败：" + ex.Message,
                    "立即触发补传", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ══════════════════════════════════════════════════════
        // 导出诊断包：logs/ 当天文件 + 数据库 + 配置 → zip
        // ══════════════════════════════════════════════════════

        private void ExportDiagnostic()
        {
            using var dlg = new SaveFileDialog
            {
                Filter   = "诊断包|*.zip",
                FileName = $"diag_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string tempDir = Path.Combine(Path.GetTempPath(),
                "envdc_diag_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1) 当天日志（NLog 一般写在 logs\ 下）
                string logsDir = Path.Combine(baseDir, "logs");
                if (Directory.Exists(logsDir))
                {
                    string dstLogs = Path.Combine(tempDir, "logs");
                    Directory.CreateDirectory(dstLogs);
                    string today = DateTime.Now.ToString("yyyy-MM-dd");
                    foreach (var f in Directory.EnumerateFiles(logsDir, "*", SearchOption.AllDirectories))
                    {
                        // 仅打包当天的日志（按文件名匹配 yyyy-MM-dd 或最近 24h 修改）
                        string name = Path.GetFileName(f);
                        bool nameMatch = name.Contains(today);
                        bool recent = (DateTime.Now - File.GetLastWriteTime(f)).TotalHours <= 24;
                        if (!nameMatch && !recent) continue;
                        try
                        {
                            string rel = f.Substring(logsDir.Length).TrimStart(Path.DirectorySeparatorChar);
                            string dst = Path.Combine(dstLogs, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dst));
                            File.Copy(f, dst, true);
                        }
                        catch (Exception ex) { Log.Debug(ex, "复制日志失败 {0}", f); }
                    }
                }

                // 2) 数据库
                string dbName = ConfigurationManager.AppSettings["DbPath"] ?? "EnvDataCollector.db";
                string dbPath = Path.Combine(baseDir, dbName);
                if (File.Exists(dbPath))
                {
                    try { File.Copy(dbPath, Path.Combine(tempDir, Path.GetFileName(dbPath)), true); }
                    catch (Exception ex) { Log.Debug(ex, "复制 db 失败"); }
                }

                // 3) 配置文件
                foreach (var cfg in new[] { "App.config", "EnvDataCollector.exe.config", "NLog.config" })
                {
                    string p = Path.Combine(baseDir, cfg);
                    if (File.Exists(p))
                    {
                        try { File.Copy(p, Path.Combine(tempDir, cfg), true); } catch { }
                    }
                }

                // 4) 打包
                if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                ZipFile.CreateFromDirectory(tempDir, dlg.FileName);

                MessageBox.Show($"诊断包已生成：\n{dlg.FileName}",
                    "导出诊断包", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ExportDiagnostic 异常");
                MessageBox.Show("导出失败：" + ex.Message,
                    "导出诊断包", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
