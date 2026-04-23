using System.Windows.Forms;

namespace EnvDataCollector.Forms.Panels
{
    public class CameraConfigPanel : PanelBase
    {
        private ComboBox      _cmbDevice;
        private TextBox       _txtIp, _txtPort, _txtUser, _txtPwd, _txtPath, _txtUrl;
        private NumericUpDown _numCh, _numPre, _numPost;
        private Label         _lblResult;

        public CameraConfigPanel(MainForm main) { BuildUI(); }

        private void BuildUI()
        {
            _lblResult = UIHelper.ResultLabel();
            _cmbDevice = UIHelper.MakeCombo();

            _txtIp   = UIHelper.MakeTextBox("摄像头 IP 地址");
            _txtPort = UIHelper.MakeTextBox(); _txtPort.Text = "8000";
            _txtUser = UIHelper.MakeTextBox("登录用户名");
            _txtPwd  = UIHelper.MakeTextBox(password: true);
            _numCh   = UIHelper.MakeNumeric(1, 32, 1);
            _numPre  = UIHelper.MakeNumeric(0, 600, 30);
            _numPost = UIHelper.MakeNumeric(0, 600, 120);
            _txtPath = UIHelper.MakeTextBox(); _txtPath.Text = @"images\";
            _txtUrl  = UIHelper.MakeTextBox(); _txtUrl.Text  = "http://localhost:8088/images";

            var tbl = UIHelper.MakeFormTable(labelWidth: 110);
            tbl.AddRow("洗车机设备",   _cmbDevice);
            tbl.AddRow("摄像头 IP",    _txtIp);
            tbl.AddRow("端口",         _txtPort);
            tbl.AddRow("用户名",       _txtUser);
            tbl.AddRow("密码",         _txtPwd);
            tbl.AddRow("通道号",       _numCh);
            tbl.AddRow("匹配前置(s)",  _numPre);
            tbl.AddRow("匹配后置(s)",  _numPost);
            tbl.AddRow("图片保存路径", _txtPath);
            tbl.AddRow("图片访问 URL", _txtUrl);

            var btnSave = UIHelper.MakeBtn("💾 保存");
            var btnTest = UIHelper.MakeBtn("📷 测试摄像头", UIHelper.C.Success);
            btnSave.Click += (s, e) => Tip("功能已禁用");
            btnTest.Click += (s, e) => Tip("功能已禁用");
            tbl.AddBtnRow(btnSave, btnTest, _lblResult);

            Controls.Add(UIHelper.WrapFormPanel(tbl, height: tbl.RowCount * 36 + 50));
        }

        public override void RefreshData() { }
    }
}
