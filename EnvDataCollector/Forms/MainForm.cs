using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Forms.Panels;
using EnvDataCollector.Services;
using EnvDataCollector.Services.Hk;
using NLog;

namespace EnvDataCollector.Forms
{
    public partial class MainForm : Form, ModbusServer.MainStatusProvider
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        // ── 长期运行的服务 ────────────────────────────────────
        public readonly OpcUaService       Opc            = new();
        public readonly CameraService      Cam            = new();
        public readonly ImageHttpServer    ImageHttp      = new();
        public readonly RunRecordBuilder   RunBuilder     = new();
        public readonly SnapshotWriter     SnapWriter     = new();
        public readonly TokenService       TokenSvc       = new();
        public readonly PushWorker         Pusher         = new();
        public readonly StatusUploadWorker StatusUploader = new();
        public readonly CleanupWorker      Cleanup        = new();
        public readonly ModbusServer       Modbus         = new();

        private readonly OutboxRepository _outboxRepo = new();

        private readonly Dictionary<string, UserControl> _panels = new();
        private UserControl _current;
        private readonly System.Windows.Forms.Timer _statusTimer;

        public MainForm()
        {
            InitializeComponent();
            this.Text = "原料大棚洗车与除尘数据采集程序";
            this.MinimumSize = new Size(1100, 680);

            Opc.OnSessionState += (srvId, connected) =>
            {
                Log.Info($"[OpcUA] Server {srvId} {(connected ? "已连接" : "已断开")}");
            };
            Opc.Start();
            SnapWriter.Start(Opc);
            ImageHttp.Start();
            Cam.Start();
            RunBuilder.Start();
            Pusher.Start(TokenSvc);
            StatusUploader.Start();
            Cleanup.Start();
            Modbus.Start(this);

            InitPanels();
            _navTree.AfterSelect += (s, e) => Navigate(e.Node?.Name);
            _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _statusTimer.Tick += (s, e) => UpdateStatus();
            _statusTimer.Start();

            Navigate("Dashboard");
            Log.Info("[UI] 主窗体初始化完成");
        }

        private void InitPanels()
        {
            _panels["Dashboard"]    = new DashboardPanel(this);
            _panels["OpcUaConfig"]  = new OpcUaConfigPanel(this);
            _panels["DeviceManage"] = new DeviceManagePanel(this);
            _panels["VarBrowser"]   = new VariableBrowserPanel(this);
            _panels["CameraConfig"] = new CameraConfigPanel(this);
            _panels["PlateEvent"]   = new PlateEventPanel(this);
            _panels["PlatformApi"]  = new PlatformApiPanel(this);
            _panels["Outbox"]       = new OutboxPanel(this);
            _panels["RunRecord"]    = new RunRecordPanel(this);
            _panels["Cleanup"]      = new CleanupPanel(this);
            _panels["Modbus"]       = new ModbusPanel(this);
            _panels["Log"]          = new LogPanel(this);

            foreach (var p in _panels.Values)
            {
                p.Dock = DockStyle.Fill;
                _contentPanel.Controls.Add(p);
                p.Visible = false;
            }
        }

        private void Navigate(string key)
        {
            if (key == null || !_panels.TryGetValue(key, out var panel)) return;
            if (_current != null) _current.Visible = false;
            _current = panel;
            _current.Visible = true;
            if (_current is IRefreshable r) r.RefreshData();
        }

        private void UpdateStatus()
        {
            UIHelper.SafeInvoke(this, () =>
            {
                int disc = Opc.DisconnectedCount();
                _lblOpc.Text      = disc > 0 ? $"OpcUA ⚠{disc}" : "OpcUA ✓";
                _lblOpc.ForeColor = disc > 0 ? Color.OrangeRed : Color.DarkGreen;

                int camActive = Cam.ActiveCount;
                _lblCam.Text = Cam.Running
                    ? (camActive > 0 ? $"摄像头 ✓{camActive}" : "摄像头 ⚠0")
                    : "摄像头 ⏹";
                _lblCam.ForeColor = Cam.Running && camActive > 0
                    ? Color.DarkGreen
                    : (Cam.Running ? Color.OrangeRed : Color.Gray);

                try
                {
                    var (failed, pending) = _outboxRepo.GetCounts();
                    _lblPush.Text      = failed > 0 ? $"推送 ⚠{failed}" : "推送 ✓";
                    _lblPush.ForeColor = failed > 0 ? Color.OrangeRed : Color.DarkGreen;
                    _lblPending.Text   = pending > 0 ? $"积压 {pending}" : "积压 -";
                    _lblPending.ForeColor = pending > 0 ? Color.DarkOrange : Color.Gray;
                }
                catch
                {
                    _lblPush.Text = "推送 -"; _lblPush.ForeColor = Color.Gray;
                    _lblPending.Text = "积压 -"; _lblPending.ForeColor = Color.Gray;
                }
                _lblTime.Text = DateTime.Now.ToString("HH:mm:ss");
            });
        }

        // ── ModbusServer.MainStatusProvider 接口 ─────────────
        public int OpcUaDisconnectedCount() => Opc?.DisconnectedCount() ?? 0;
        public int CameraActiveCount()      => Cam?.ActiveCount ?? 0;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _statusTimer.Stop();
            try { Modbus?.Stop();         } catch { }
            try { Cleanup?.Stop();        } catch { }
            try { StatusUploader?.Stop(); } catch { }
            try { Pusher?.Stop();         } catch { }
            try { RunBuilder?.Stop();     } catch { }
            try { Cam?.Stop();            } catch { }
            try { ImageHttp?.Stop();      } catch { }
            try { SnapWriter?.Stop();     } catch { }
            try { Opc?.Dispose();         } catch { }
            base.OnFormClosing(e);
        }
    }
}
