using System;
using System.Drawing;
using System.Windows.Forms;

namespace EnvDataCollector.Forms.Panels
{
    public class DashboardPanel : PanelBase
    {
        private readonly MainForm _main;
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
            btnRetry.Click += (s, e) => Tip("功能已禁用");
            btnDiag.Click  += (s, e) => Tip("功能已禁用");
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
                int disc = _main.Opc.DisconnectedCount();
                _lblOpc.Text = $"OPC UA 断线\n{disc}";
            });
        }

        public override void RefreshData() => UpdateCards();
    }
}
