using System.Drawing;
using System.Windows.Forms;

namespace EnvDataCollector.Forms.Panels
{
    public class ModbusPanel : PanelBase
    {
        private CheckBox     _chkEnabled;
        private TextBox      _txtIp, _txtPort;
        private ComboBox     _cmbHb;
        private DataGridView _gridCoils, _gridRegs;

        private static readonly string[] CoilNames = {
            "AppRunning", "HeartbeatBit", "OpcUaAnyDisconnected",
            "CameraAnyOffline", "PushHasFailed", "PushHasPending" };
        private static readonly string[] RegNames = {
            "HeartbeatCounter", "OpcUaDisconnectedCount", "CameraOfflineCount",
            "PushFailedCount", "PushPendingCount", "PushOldestPendingMin" };

        public ModbusPanel(MainForm main) { BuildUI(); }

        private void BuildUI()
        {
            _chkEnabled = new CheckBox
            {
                Text = "启用 Modbus TCP 状态反馈",
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
            };
            _txtIp   = UIHelper.MakeTextBox(); _txtIp.Text = "0.0.0.0";
            _txtPort = UIHelper.MakeTextBox(); _txtPort.Text = "1502";
            _cmbHb   = UIHelper.MakeCombo("Counter", "Bit");

            var btnSave = UIHelper.MakeBtn("💾 保存（重启生效）");
            btnSave.Click += (s, e) => Tip("功能已禁用");

            var tbl = UIHelper.MakeFormTable(labelWidth: 100);
            tbl.AddRowSpan(_chkEnabled, height: 32);
            tbl.AddRow("监听 IP",  _txtIp);
            tbl.AddRow("端口",     _txtPort);
            tbl.AddRow("心跳模式", _cmbHb);
            tbl.AddRowSpan(btnSave, height: 36);

            var cfgPanel = UIHelper.WrapFormPanel(tbl, height: 200,
                padding: new Padding(16, 12, 16, 8));

            var previewLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1
            };
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var leftPanel  = BuildPreviewPane("线圈 Coil（地址 00001 起）", out _gridCoils);
            var rightPanel = BuildPreviewPane("保持寄存器 Holding（地址 40001 起）", out _gridRegs);

            for (int i = 0; i < CoilNames.Length; i++)
                _gridCoils.Rows.Add($"0000{i + 1}", CoilNames[i], "-");
            for (int i = 0; i < RegNames.Length; i++)
                _gridRegs.Rows.Add($"4000{i + 1}", RegNames[i], "-");

            previewLayout.Controls.Add(leftPanel,  0, 0);
            previewLayout.Controls.Add(rightPanel, 1, 0);

            Controls.Add(previewLayout);
            Controls.Add(UIHelper.MakeSeparator());
            Controls.Add(cfgPanel);
        }

        private static Panel BuildPreviewPane(string title, out DataGridView grid)
        {
            var g = UIHelper.MakeGrid();
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Addr",  HeaderText = "地址",   Width = 70 });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name",  HeaderText = "名称",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "当前值", Width = 80 });
            grid = g;

            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 4, 0) };
            p.Controls.Add(g);
            p.Controls.Add(UIHelper.MakeSectionHeader(title));
            return p;
        }

        public override void RefreshData() { }
    }
}
