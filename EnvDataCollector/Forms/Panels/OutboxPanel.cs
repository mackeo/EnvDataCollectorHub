using System;
using System.Windows.Forms;

namespace EnvDataCollector.Forms.Panels
{
    public class OutboxPanel : PanelBase
    {
        private DataGridView   _grid;
        private ComboBox       _cmbStatus;
        private DateTimePicker _dtFrom, _dtTo;
        private Label          _lblCount;

        public OutboxPanel(MainForm main) { BuildUI(); }

        private void BuildUI()
        {
            _lblCount  = UIHelper.ResultLabel();
            _dtFrom    = UIHelper.MakeDatePicker(DateTime.Today);
            _dtTo      = UIHelper.MakeDatePicker(DateTime.Today.AddDays(1).AddSeconds(-1));
            _cmbStatus = UIHelper.MakeToolbarCombo(85, "全部", "Pending", "Failed", "Success");

            var btnLoad   = UIHelper.MakeBtn("🔍 查询");
            var btnRetry  = UIHelper.MakeBtn("⚡ 批量重试", UIHelper.C.Success);
            var btnExport = UIHelper.MakeBtn("📥 导出失败", UIHelper.C.Purple);
            btnLoad.Click   += (s, e) => Tip("功能已禁用");
            btnRetry.Click  += (s, e) => Tip("功能已禁用");
            btnExport.Click += (s, e) => Tip("功能已禁用");

            var toolbar = UIHelper.MakeToolbar(
                UIHelper.InlineLabel("从:"), _dtFrom,
                UIHelper.InlineLabel("至:"), _dtTo,
                UIHelper.InlineLabel("状态:"), _cmbStatus,
                btnLoad, btnRetry, btnExport, _lblCount);

            _grid = UIHelper.MakeGrid();
            _grid.Columns.AddRange(
                UIHelper.Col("Id",      "ID",       65),
                UIHelper.Col("Type",    "类型",    110),
                UIHelper.Col("Status",  "状态",     70),
                UIHelper.Col("Retry",   "重试",     45),
                UIHelper.Col("Created", "创建时间",155),
                UIHelper.Col("Next",    "下次重试",155),
                UIHelper.Col("Error",   "错误信息",  0, true));

            Controls.Add(_grid);
            Controls.Add(toolbar);
        }

        public override void RefreshData() { }
    }
}
