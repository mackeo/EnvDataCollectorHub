using System;
using System.Drawing;
using System.Windows.Forms;
using NLog;
using NLog.Targets;

namespace EnvDataCollector.Forms.Panels
{
    public class LogPanel : PanelBase
    {
        private TextBox  _txtLog;
        private ComboBox _cmbLevel;
        private readonly System.Windows.Forms.Timer _poll;

        public LogPanel(MainForm main)
        {
            BuildUI();
            _poll = new System.Windows.Forms.Timer { Interval = 2000 };
            _poll.Tick += (s, e) => PollLogs();
            _poll.Start();
        }

        private void BuildUI()
        {
            _cmbLevel = UIHelper.MakeToolbarCombo(88, "ALL", "INFO", "WARN", "ERROR");
            _cmbLevel.SelectedIndexChanged += (s, e) => PollLogs();

            var btnClear  = UIHelper.MakeBtn("🗑 清空");
            var btnExport = UIHelper.MakeBtn("📦 导出", UIHelper.C.Purple);
            btnClear.Click  += (s, e) => _txtLog.Clear();
            btnExport.Click += (s, e) => ExportLog();

            var toolbar = UIHelper.MakeToolbar(
                UIHelper.InlineLabel("级别:"), _cmbLevel, btnClear, btnExport);

            _txtLog = new TextBox
            {
                Dock       = DockStyle.Fill,
                Multiline  = true,
                ReadOnly   = true,
                ScrollBars = ScrollBars.Both,
                WordWrap   = false,
                Font       = new Font("Consolas", 9f),
                BackColor  = Color.FromArgb(25, 25, 35),
                ForeColor  = Color.FromArgb(180, 220, 140)
            };

            Controls.Add(_txtLog);
            Controls.Add(toolbar);
        }

        private void PollLogs()
        {
            try
            {
                var target = LogManager.Configuration?.FindTargetByName<MemoryTarget>("mem");
                if (target == null) return;
                string filter = _cmbLevel.SelectedItem?.ToString() ?? "ALL";
                var sb = new System.Text.StringBuilder();
                foreach (string line in target.Logs)
                    if (filter == "ALL" || line.Contains($"[{filter}]"))
                        sb.AppendLine(line);
                string text = sb.ToString();
                UIHelper.SafeInvoke(this, () =>
                {
                    if (_txtLog.Text == text) return;
                    _txtLog.Text = text;
                    _txtLog.SelectionStart = _txtLog.TextLength;
                    _txtLog.ScrollToCaret();
                });
            }
            catch { }
        }

        private void ExportLog()
        {
            using var dlg = new SaveFileDialog
                { Filter = "TXT|*.txt", FileName = $"log_{DateTime.Now:yyyyMMdd}.txt" };
            if (dlg.ShowDialog() == DialogResult.OK)
                System.IO.File.WriteAllText(dlg.FileName, _txtLog.Text, System.Text.Encoding.UTF8);
        }

        public override void RefreshData() => PollLogs();
    }
}
