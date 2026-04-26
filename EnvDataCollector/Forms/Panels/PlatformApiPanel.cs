using System;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Services;
using Newtonsoft.Json;
using NLog;

namespace EnvDataCollector.Forms.Panels
{
    public class PlatformApiPanel : PanelBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly MainForm _main;
        private readonly AppSettingRepository _settings = new();

        private TextBox       _txtStatus, _txtEvent, _txtToken, _txtUser, _txtPwd;
        private TextBox       _txtImageUpload;
        private CheckBox      _chkToken;
        private NumericUpDown _numTimeout;
        private NumericUpDown _numStatusInterval;
        private ComboBox      _cmbStatMode;
        private NumericUpDown _numPlateWait;
        private Label         _lblResult;

        public PlatformApiPanel(MainForm main) { _main = main; BuildUI(); }

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
            btnSave.Click  += (s, e) => SaveSettings();
            btnToken.Click += (s, e) => RefreshToken(btnToken);
            btnTest.Click  += (s, e) => TestPush(btnTest);
            tbl.AddBtnRow(btnSave, btnToken, btnTest, _lblResult);

            Controls.Add(UIHelper.WrapFormPanel(tbl, height: tbl.RowCount * 36 + 30,
                padding: new Padding(20, 12, 20, 8)));
        }

        // ══════════════════════════════════════════════════════
        // 加载 / 保存
        // ══════════════════════════════════════════════════════

        public override void RefreshData()
        {
            _txtStatus.Text         = _settings.Get(SK.StatusApiUrl, "");
            _txtEvent.Text          = _settings.Get(SK.EventApiUrl, "");
            _txtImageUpload.Text    = _settings.Get(SK.ImageUploadUrl, "");
            _chkToken.Checked       = _settings.Get<int>(SK.TokenEnabled, 0) == 1;
            _txtToken.Text          = _settings.Get(SK.TokenApiUrl, "");
            _txtUser.Text           = _settings.Get(SK.TokenUsername, "");
            string passEnc          = _settings.Get(SK.TokenPassword, "");
            _txtPwd.Text            = string.IsNullOrEmpty(passEnc) ? "" : CryptoHelper.Decrypt(passEnc);
            _numTimeout.Value       = ClampNum(_settings.Get<int>(SK.HttpTimeoutSec, 30), 5, 120);
            _numStatusInterval.Value= ClampNum(_settings.Get<int>(SK.StatusUploadIntervalSec, 60), 10, 3600);
            _cmbStatMode.SelectedItem = _settings.Get(SK.EventStatMode, "Avg");
            _numPlateWait.Value     = ClampNum(_settings.Get<int>(SK.PlateWaitMaxSec, 180), -1, 600);
        }

        private static decimal ClampNum(int v, int lo, int hi) =>
            v < lo ? lo : (v > hi ? hi : v);

        private void SaveSettings()
        {
            try
            {
                _settings.Set(SK.StatusApiUrl,            _txtStatus.Text.Trim());
                _settings.Set(SK.EventApiUrl,             _txtEvent.Text.Trim());
                _settings.Set(SK.ImageUploadUrl,          _txtImageUpload.Text.Trim());
                _settings.Set(SK.TokenEnabled,            _chkToken.Checked ? 1 : 0);
                _settings.Set(SK.TokenApiUrl,             _txtToken.Text.Trim());
                _settings.Set(SK.TokenUsername,           _txtUser.Text.Trim());
                _settings.Set(SK.TokenPassword,
                    string.IsNullOrEmpty(_txtPwd.Text) ? "" : CryptoHelper.Encrypt(_txtPwd.Text));
                _settings.Set(SK.HttpTimeoutSec,          (int)_numTimeout.Value);
                _settings.Set(SK.StatusUploadIntervalSec, (int)_numStatusInterval.Value);
                _settings.Set(SK.EventStatMode,           _cmbStatMode.SelectedItem?.ToString() ?? "Avg");
                _settings.Set(SK.PlateWaitMaxSec,         (int)_numPlateWait.Value);

                SetOk(_lblResult, "✅ 已保存（部分配置需重启服务生效）");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PlatformApi 保存失败");
                SetError(_lblResult, "❌ 保存失败：" + ex.Message);
            }
        }

        // ══════════════════════════════════════════════════════
        // 刷新 Token / 测试推送
        // ══════════════════════════════════════════════════════

        private void RefreshToken(Button btn)
        {
            if (_main?.TokenSvc == null) { Tip("TokenService 未初始化"); return; }
            if (string.IsNullOrWhiteSpace(_txtToken.Text)) { Tip("请先填 Token URL 并保存"); return; }

            btn.Enabled = false;
            SetInfo(_lblResult, "正在刷新 Token…");

            Task.Run(() =>
            {
                bool ok = false; string err = null; DateTime? exp = null;
                try
                {
                    ok = _main.TokenSvc.Refresh();
                    err = _main.TokenSvc.LastError;
                    exp = _main.TokenSvc.ExpiresAt;
                }
                catch (Exception ex) { err = ex.Message; }
                BeginInvoke((Action)(() =>
                {
                    btn.Enabled = true;
                    if (ok) SetOk(_lblResult,    $"✅ 刷新成功，过期：{exp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(未知)"}");
                    else    SetError(_lblResult, "❌ 刷新失败：" + (err ?? "未知错误"));
                }));
            });
        }

        private void TestPush(Button btn)
        {
            string url = _txtStatus.Text.Trim();
            if (string.IsNullOrWhiteSpace(url)) { Tip("请先填状态上报 URL"); return; }

            btn.Enabled = false;
            SetInfo(_lblResult, "正在发送测试 POST…");

            int timeout = (int)_numTimeout.Value;
            bool tokenEnabled = _chkToken.Checked;
            var token = _main?.TokenSvc;

            Task.Run(() =>
            {
                int code = 0;
                string respText = null, err = null;
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
                    using var req = new HttpRequestMessage(HttpMethod.Post, url);
                    var payload = new {
                        deviceCode = "TEST",
                        time       = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        online     = 1, startup = 0,
                        currents = 0.0, waterPressure = 0.0, flowQuantity = 0.0,
                        statMode = "Avg", samples = 0,
                        note = "test ping from EnvDataCollector"
                    };
                    req.Content = new StringContent(JsonConvert.SerializeObject(payload),
                        Encoding.UTF8, "application/json");
                    if (tokenEnabled && token != null) token.ApplyBearer(req);
                    var resp = http.SendAsync(req).GetAwaiter().GetResult();
                    code = (int)resp.StatusCode;
                    respText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex) { err = ex.Message; }

                BeginInvoke((Action)(() =>
                {
                    btn.Enabled = true;
                    if (err != null) SetError(_lblResult, "❌ 异常：" + err);
                    else if (code >= 200 && code < 300)
                        SetOk(_lblResult,
                            $"✅ HTTP {code}，响应：{Truncate(respText, 100)}");
                    else
                        SetError(_lblResult,
                            $"❌ HTTP {code}：{Truncate(respText, 200)}");
                }));
            });
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "(空)" : (s.Length <= max ? s : s.Substring(0, max));
    }
}
