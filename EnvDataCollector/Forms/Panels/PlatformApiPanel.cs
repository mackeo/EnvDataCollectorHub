using System.Windows.Forms;
using System;
using System.Drawing;

namespace EnvDataCollector.Forms.Panels
{
    public class PlatformApiPanel : PanelBase
    {
        private TextBox       _txtStatus, _txtEvent, _txtToken, _txtUser, _txtPwd;
        private TextBox       _txtImageUpload;
        private CheckBox      _chkToken;
        private NumericUpDown _numTimeout;
        private NumericUpDown _numStatusInterval;
        private ComboBox      _cmbStatMode;
        private NumericUpDown _numPlateWait;
        private Label         _lblResult;

        public PlatformApiPanel(MainForm main) { BuildUI(); }

        private void BuildUI()
        {
            _lblResult     = UIHelper.ResultLabel();
            _txtStatus     = UIHelper.MakeTextBox("http://host/qtcom/.../1555155601/1555155601");
            _txtEvent      = UIHelper.MakeTextBox("http://host/qtcom/.../1555155602/1555155602");
            _txtImageUpload= UIHelper.MakeTextBox("http://host/api/upload/image");
            _chkToken      = UIHelper.MakeCheck("启用 JWT Token 鉴权");
            _txtToken      = UIHelper.MakeTextBox("http://host/qtcom/app/jwt/get/token");
            _txtUser       = UIHelper.MakeTextBox();
            _txtPwd        = UIHelper.MakeTextBox(password: true);
            _numTimeout    = UIHelper.MakeNumeric(5, 120, 30);
            _numStatusInterval = UIHelper.MakeNumeric(10, 3600, 60);
            _cmbStatMode   = UIHelper.MakeCombo("Avg", "Max", "Min", "Median");
            _numPlateWait  = UIHelper.MakeNumeric(-1, 600, 180);

            var tbl = UIHelper.MakeFormTable(labelWidth: 140);
            tbl.AddRow("状态上报 URL",      _txtStatus);
            tbl.AddRow("事件上报 URL",      _txtEvent);
            tbl.AddRow("图片上传 URL",      _txtImageUpload);
            tbl.AddRowSpan(_chkToken, height: 32);
            tbl.AddRow("Token URL",        _txtToken);
            tbl.AddRow("Token 用户名",     _txtUser);
            tbl.AddRow("Token 密码",       _txtPwd);
            tbl.AddRow("HTTP 超时(s)",     _numTimeout);

            tbl.AddRowSpan(UIHelper.MakeSectionHeader("数据上传策略"), height: 28);
            tbl.AddRow("状态上传间隔(s)",   _numStatusInterval);
            tbl.AddRow("事件统计字段",       _cmbStatMode);
            tbl.AddRow("车牌最大等待(s)",   _numPlateWait);

            var note = new Label
            {
                Text      = "车牌等待: -1=无限等待直到识别；≥0=超时后空车牌上传。图片上传: 洗车机事件推送前自动调用",
                ForeColor = UIHelper.C.TextMuted,
                AutoSize  = true,
                Padding   = new Padding(0, 2, 0, 0)
            };
            tbl.AddRowSpan(note, height: 28);

            var btnSave  = UIHelper.MakeBtn("💾 保存");
            var btnToken = UIHelper.MakeBtn("🔑 刷新 Token",  UIHelper.C.Success);
            var btnTest  = UIHelper.MakeBtn("📤 测试推送",    UIHelper.C.Purple);
            btnSave.Click  += (s, e) => Tip("功能已禁用");
            btnToken.Click += (s, e) => Tip("功能已禁用");
            btnTest.Click  += (s, e) => Tip("功能已禁用");
            tbl.AddBtnRow(btnSave, btnToken, btnTest, _lblResult);

            Controls.Add(UIHelper.WrapFormPanel(tbl, height: tbl.RowCount * 36 + 30,
                padding: new Padding(20, 12, 20, 8)));
        }

        public override void RefreshData() { }
    }
}
