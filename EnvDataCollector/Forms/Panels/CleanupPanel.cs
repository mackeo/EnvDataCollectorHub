using System.Drawing;
using System.Windows.Forms;

namespace EnvDataCollector.Forms.Panels
{
    public class CleanupPanel : PanelBase
    {
        private NumericUpDown _numDays, _numHours;
        private Label         _lblResult;

        public CleanupPanel(MainForm main) { BuildUI(); }

        private void BuildUI()
        {
            _lblResult = UIHelper.ResultLabel();
            _numDays   = UIHelper.MakeNumeric(1, 365, 30);
            _numHours  = UIHelper.MakeNumeric(1, 168, 24);

            var note = new Label
            {
                Text      = "⚠ 仅清理 PushStatus=Success 的历史数据，失败/待推送数据及图片不会被清理",
                ForeColor = Color.OrangeRed,
                AutoSize  = true,
                Padding   = new Padding(0, 4, 0, 0)
            };

            var tbl = UIHelper.MakeFormTable(labelWidth: 110);
            tbl.AddRow("保留天数",    _numDays);
            tbl.AddRow("清理周期(h)", _numHours);
            tbl.AddRowSpan(note, height: 40);

            var btnSave  = UIHelper.MakeBtn("💾 保存");
            var btnClean = UIHelper.MakeBtn("🗑 立即清理", UIHelper.C.Danger);
            btnSave.Click  += (s, e) => Tip("功能已禁用");
            btnClean.Click += (s, e) => Tip("功能已禁用");
            tbl.AddBtnRow(btnSave, btnClean, _lblResult);

            Controls.Add(UIHelper.WrapFormPanel(tbl, height: 200,
                padding: new Padding(20, 16, 20, 8)));
        }

        public override void RefreshData() { }
    }
}
