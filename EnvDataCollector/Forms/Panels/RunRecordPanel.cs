using System;
using System.Windows.Forms;

namespace EnvDataCollector.Forms.Panels
{
    public class RunRecordPanel : PanelBase
    {
        private DataGridView   _grid;
        private DateTimePicker _dtFrom, _dtTo;
        private TextBox        _txtCode, _txtPlate;
        private ComboBox       _cmbStatus;

        public RunRecordPanel(MainForm main) { BuildUI(); }

        private void BuildUI()
        {
            _dtFrom    = UIHelper.MakeDatePicker(DateTime.Today);
            _dtTo      = UIHelper.MakeDatePicker(DateTime.Today.AddDays(1).AddSeconds(-1));
            _txtCode   = UIHelper.MakeToolbarTextBox(100, "设备编码");
            _txtPlate  = UIHelper.MakeToolbarTextBox(100, "车牌");
            _cmbStatus = UIHelper.MakeToolbarCombo(78, "全部", "Pending", "Failed", "Success");

            var btnQuery  = UIHelper.MakeBtn("🔍 查询");
            var btnResend = UIHelper.MakeBtn("⚡ 重发选中",      UIHelper.C.Success);
            var btnAdmin  = UIHelper.MakeBtn("✅ 管理员标记成功", UIHelper.C.Purple);
            btnQuery.Click  += (s, e) => Tip("功能已禁用");
            btnResend.Click += (s, e) => Tip("功能已禁用");
            btnAdmin.Click  += (s, e) => Tip("功能已禁用");

            var toolbar = UIHelper.MakeToolbar(
                UIHelper.InlineLabel("从:"), _dtFrom,
                UIHelper.InlineLabel("至:"), _dtTo,
                _txtCode, _txtPlate, _cmbStatus,
                btnQuery, btnResend, btnAdmin);

            _grid = UIHelper.MakeGrid();
            _grid.Columns.AddRange(
                UIHelper.Col("Id",    "ID",       60),
                UIHelper.Col("Code",  "设备编码",100),
                UIHelper.Col("Type",  "类型",     70),
                UIHelper.Col("Start", "开始时间",155),
                UIHelper.Col("End",   "结束时间",155),
                UIHelper.Col("Sec",   "秒",        55),
                UIHelper.Col("Plate", "车牌",      90),
                UIHelper.Col("Push",  "推送",       0, true));

            Controls.Add(_grid);
            Controls.Add(toolbar);
        }

        public override void RefreshData() { }
    }
}
