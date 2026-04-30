using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using EnvDataCollector.Services;
using EnvDataCollector.Services.Hk;
using NLog;

namespace EnvDataCollector.Forms.Panels
{
    public class CameraConfigPanel : PanelBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly MainForm                _main;
        private readonly CameraConfigRepository  _repo    = new();
        private readonly DeviceRepository        _devRepo = new();

        private ComboBox      _cmbDevice;
        private TextBox       _txtIp, _txtUser, _txtPwd, _txtPath, _txtUrl;
        private NumericUpDown _numPort, _numCh, _numPre, _numPost;
        private CheckBox      _chkEnabled;
        private DataGridView  _grid;
        private Label         _lblResult;
        private Button        _btnToggle;

        private int  _editId      = -1;
        private bool _editEnabled = true;
        private bool _refreshing;

        public CameraConfigPanel(MainForm main) { _main = main; BuildUI(); }

        private void BuildUI()
        {
            // ── 工具栏 ──────────────────────────────────────
            _lblResult = UIHelper.ResultLabel();
            var btnNew   = UIHelper.MakeBtn("➕ 新建", UIHelper.C.Dark);
            var btnSave  = UIHelper.MakeBtn("💾 保存", UIHelper.C.Primary);
            var btnDel   = UIHelper.MakeBtn("🗑 删除", UIHelper.C.Danger);
            _btnToggle   = UIHelper.MakeBtn("⏸ 禁用", UIHelper.C.Warning);
            var btnTest  = UIHelper.MakeBtn("📷 测试凭证", UIHelper.C.Success);
            var btnStart = UIHelper.MakeBtn("▶ 启动采集", UIHelper.C.Success);
            var btnStop  = UIHelper.MakeBtn("⏹ 停止采集", UIHelper.C.Danger);

            btnNew.Click     += (s, e) => NewRecord();
            btnSave.Click    += (s, e) => Save();
            btnDel.Click     += (s, e) => Delete();
            _btnToggle.Click += (s, e) => ToggleEnabled();
            btnTest.Click    += (s, e) => TestCredential(btnTest);
            btnStart.Click   += (s, e) => StartCapture();
            btnStop.Click    += (s, e) => StopCapture();

            var toolbar = UIHelper.MakeToolbar(
                btnNew, btnSave, btnDel, _btnToggle, btnTest, btnStart, btnStop, _lblResult);

            // ── 表单（4列） ─────────────────────────────────
            _cmbDevice = UIHelper.MakeCombo();
            _txtIp     = UIHelper.MakeTextBox("摄像头 IP");
            _numPort   = UIHelper.MakeNumeric(1, 65535, 8000);
            _numCh     = UIHelper.MakeNumeric(1, 32, 1);
            _txtUser   = UIHelper.MakeTextBox("登录用户名");
            _txtPwd    = UIHelper.MakeTextBox(password: true);
            _numPre    = UIHelper.MakeNumeric(0, 600, 30);
            _numPost   = UIHelper.MakeNumeric(0, 600, 120);
            _txtPath   = UIHelper.MakeTextBox(@"images\");
            _txtUrl    = UIHelper.MakeTextBox("http://localhost:8088/images");
            _chkEnabled = UIHelper.MakeCheck("启用此摄像头", true);

            var tbl = UIHelper.MakeFormTable4(lw1: 85, fw1: 200, lw2: 95);
            tbl.AddRow4("洗车机设备",   _cmbDevice, "摄像头 IP",   _txtIp);
            tbl.AddRow4("端口",         _numPort,   "通道号",      _numCh);
            tbl.AddRow4("用户名",       _txtUser,   "密码",        _txtPwd);
            tbl.AddRow4("匹配前置(秒)", _numPre,    "匹配后置(秒)",_numPost);
            AddSingleRow(tbl, "图片保存路径", _txtPath);
            AddSingleRow(tbl, "图片访问 URL(弃用)", _txtUrl);
            tbl.AddRow4Span(_chkEnabled, height: 30);

            var formPanel = UIHelper.WrapFormPanel(tbl, height: 7 * 28 + 40);

            // ── 列表 ────────────────────────────────────────
            _grid = UIHelper.MakeGrid();
            _grid.Columns.AddRange(
                UIHelper.Col("Id",   "ID",          45),
                UIHelper.Col("Dev",  "设备",        130),
                UIHelper.Col("Ip",   "IP",          130),
                UIHelper.Col("Port", "端口",         60),
                UIHelper.Col("Ch",   "通道",         50),
                UIHelper.Col("User", "用户名",      110),
                UIHelper.Col("Win",  "匹配窗(前/后)",100),
                UIHelper.Col("Path", "图片路径",     0, true),
                UIHelper.Col("Ena",  "状态",         60));
            _grid.SelectionChanged += (s, e) => { if (!_refreshing) LoadRowFromCurrent(); };
            _grid.RowEnter         += (s, e) => { if (!_refreshing) LoadRowFromIndex(e.RowIndex); };
            _grid.CellClick        += (s, e) => { if (!_refreshing) LoadRowFromIndex(e.RowIndex); };

            Controls.Add(_grid);
            Controls.Add(UIHelper.MakeSeparator());
            Controls.Add(formPanel);
            Controls.Add(toolbar);
        }

        private static void AddSingleRow(TableLayoutPanel tbl, string label, Control ctrl)
        {
            int r = tbl.RowCount++;
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            var lbl = UIHelper.FormLabel(label);
            tbl.Controls.Add(lbl, 0, r);
            tbl.Controls.Add(ctrl, 1, r);
            tbl.SetColumnSpan(ctrl, 3);
            ctrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            ctrl.Margin = new Padding(3, 3, 3, 3);
        }

        // ══════════════════════════════════════════════════════
        // 选中/新建/保存/删除/启停
        // ══════════════════════════════════════════════════════

        private void LoadRowFromCurrent()
        {
            int idx = _grid.CurrentRow?.Index ?? -1;
            if (idx < 0 && _grid.SelectedRows.Count > 0) idx = _grid.SelectedRows[0].Index;
            LoadRowFromIndex(idx);
        }

        private void LoadRowFromIndex(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
            if (!int.TryParse(_grid.Rows[rowIndex].Cells["Id"].Value?.ToString(), out int id)) return;
            var c = _repo.GetById(id); if (c == null) return;
            _editId = id;

            SelectDeviceInCombo(c.DeviceId);
            _txtIp.Text        = c.Ip;
            _numPort.Value     = Clamp(c.Port, 1, 65535);
            _numCh.Value       = Clamp(c.Channel, 1, 32);
            _txtUser.Text      = c.Username;
            _txtPwd.Text       = CryptoHelper.Decrypt(c.PasswordEnc);
            _numPre.Value      = Clamp(c.MatchPreSec, 0, 600);
            _numPost.Value     = Clamp(c.MatchPostSec, 0, 600);
            _txtPath.Text      = c.ImageStorePath;
            _txtUrl.Text       = c.ImageBaseUrl;
            _chkEnabled.Checked = _editEnabled = c.Enabled == 1;
            SyncToggleBtn(_btnToggle, _editEnabled);
            SetInfo(_lblResult, "");
        }

        private void NewRecord()
        {
            _editId = -1;
            if (_cmbDevice.Items.Count > 0) _cmbDevice.SelectedIndex = 0;
            else                            _cmbDevice.SelectedIndex = -1;
            _txtIp.Text = _txtUser.Text = _txtPwd.Text = "";
            _numPort.Value = 8000;
            _numCh.Value   = 1;
            _numPre.Value  = 30;
            _numPost.Value = 120;
            _txtPath.Text  = @"images\";
            _txtUrl.Text   = "http://localhost:8088/images";
            _chkEnabled.Checked = _editEnabled = true;
            SyncToggleBtn(_btnToggle, _editEnabled);
            _grid.ClearSelection();
            if (_cmbDevice.Items.Count == 0)
                SetError(_lblResult, "⚠ 没有可绑定的洗车机设备，请先到「设备管理」创建");
            else
                SetInfo(_lblResult, "");
            _txtIp.Focus();
        }

        private void Save()
        {
            if (_cmbDevice.SelectedItem is not UIHelper.Item dev) { Tip("请选择洗车机设备"); return; }
            string ip   = _txtIp.Text.Trim();
            string user = _txtUser.Text.Trim();
            string pwd  = _txtPwd.Text;
            string path = _txtPath.Text.Trim();
            string url  = _txtUrl.Text.Trim();

            if (string.IsNullOrEmpty(ip))   { Tip("摄像头 IP 不能为空"); return; }
            if (string.IsNullOrEmpty(user)) { Tip("用户名不能为空");     return; }
            if (string.IsNullOrEmpty(pwd))  { Tip("密码不能为空");       return; }
            if (string.IsNullOrEmpty(path)) { Tip("图片保存路径不能为空"); return; }
            //if (string.IsNullOrEmpty(url))  { Tip("图片访问 URL 不能为空"); return; }
            //if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { Tip("图片访问 URL 格式不合法"); return; }

            // 一个洗车机只能绑定一条摄像头：检查是否被别的记录占用
            var exist = _repo.GetByDevice(dev.Id);
            if (exist != null && exist.Id != _editId)
            {
                Tip($"该洗车机已绑定摄像头配置（ID={exist.Id}），请直接修改那条记录或解绑");
                return;
            }

            // 确保图片目录存在
            try { EnsureDirectory(path); }
            catch (Exception ex) { Tip("创建图片目录失败：" + ex.Message); return; }

            var entity = new CameraConfigEntity
            {
                Id             = _editId > 0 ? _editId : 0,
                DeviceId       = dev.Id,
                Ip             = ip,
                Port           = (int)_numPort.Value,
                Username       = user,
                PasswordEnc    = CryptoHelper.Encrypt(pwd),
                Channel        = (int)_numCh.Value,
                Enabled        = _chkEnabled.Checked ? 1 : 0,
                MatchPreSec    = (int)_numPre.Value,
                MatchPostSec   = (int)_numPost.Value,
                ImageStorePath = path,
                ImageBaseUrl   = url.TrimEnd('/'),
                CreatedAt      = exist?.CreatedAt   // Upsert 里若已存在则不覆盖 CreatedAt
            };

            try
            {
                _repo.Upsert(entity);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存摄像头配置失败");
                SetError(_lblResult, "❌ 保存失败：" + ex.Message);
                return;
            }

            _editEnabled = entity.Enabled == 1;
            SyncToggleBtn(_btnToggle, _editEnabled);
            RefreshData();
            // Upsert 不返回 Id；按 device_id 找回当前行并选中
            var saved = _repo.GetByDevice(dev.Id);
            if (saved != null) { _editId = saved.Id; _grid.SelectRowById(_editId); }
            ReloadCaptureIfRunning();
            SetOk(_lblResult, _editEnabled ? "✅ 已保存" : "✅ 已保存（当前为禁用状态）");
        }

        private void Delete()
        {
            if (_editId <= 0) { Tip("请先选择一条记录"); return; }
            if (!Confirm("确认删除该摄像头配置？\n（历史车牌识别记录会保留作为审计留痕）")) return;

            try
            {
                _repo.Delete(_editId);
            }
            catch (Exception ex)
            {
                SetError(_lblResult, "❌ 删除失败：" + ex.Message);
                return;
            }

            _editId = -1;
            RefreshData();
            NewRecord();
            ReloadCaptureIfRunning();
            SetError(_lblResult, "🗑 已删除");
        }

        private void ToggleEnabled()
        {
            if (_editId <= 0) { Tip("请先选择一条记录"); return; }
            var c = _repo.GetById(_editId); if (c == null) return;
            c.Enabled = c.Enabled == 1 ? 0 : 1;
            _repo.Upsert(c);
            _chkEnabled.Checked = _editEnabled = c.Enabled == 1;
            SyncToggleBtn(_btnToggle, _editEnabled);
            SetResult(_lblResult,
                _editEnabled ? "✅ 已启用" : "⏸ 已禁用",
                _editEnabled ? UIHelper.C.Success : Color.OrangeRed);
            RefreshData();
            _grid.SelectRowById(_editId);
            ReloadCaptureIfRunning();
        }

        // ══════════════════════════════════════════════════════
        // 启停采集
        // ══════════════════════════════════════════════════════

        private void StartCapture()
        {
            if (_main?.Cam == null) { Tip("CameraService 未初始化"); return; }
            if (!HikSdkBootstrap.IsInitialized) { Tip("海康 SDK 未初始化"); return; }
            try
            {
                _main.Cam.Reload();   // 包含 Stop + 重新读配置 + Start
                SetOk(_lblResult, $"✅ 采集已启动，会话数 {_main.Cam.ActiveCount}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StartCapture 失败");
                SetError(_lblResult, "❌ 启动采集失败：" + ex.Message);
            }
        }

        private void StopCapture()
        {
            if (_main?.Cam == null) { Tip("CameraService 未初始化"); return; }
            try
            {
                _main.Cam.Stop();
                SetError(_lblResult, "⏹ 采集已停止");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StopCapture 失败");
                SetError(_lblResult, "❌ 停止采集失败：" + ex.Message);
            }
        }

        /// <summary>若采集正在运行，触发热重载以应用新配置</summary>
        private void ReloadCaptureIfRunning()
        {
            if (_main?.Cam != null && _main.Cam.Running)
            {
                try { _main.Cam.Reload(); }
                catch (Exception ex) { Log.Warn(ex, "采集热重载失败"); }
            }
        }

        // ══════════════════════════════════════════════════════
        // 测试凭证（NET_DVR_Login_V40 → Logout）
        // ══════════════════════════════════════════════════════

        private void TestCredential(Button btn)
        {
            string ip   = _txtIp.Text.Trim();
            string user = _txtUser.Text.Trim();
            string pwd  = _txtPwd.Text;
            int    port = (int)_numPort.Value;

            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pwd))
            { Tip("请先填写 IP / 用户名 / 密码"); return; }

            if (!HikSdkBootstrap.IsInitialized)
            { Tip("海康 SDK 未初始化：请检查 Libs\\Hksdk 目录是否随 exe 一起发布"); return; }

            btn.Enabled = false;
            SetInfo(_lblResult, "正在登录测试…");

            Task.Run(() =>
            {
                string msg; bool ok;
                try
                {
                    (ok, msg) = TryLoginAndLogout(ip, port, user, pwd);
                }
                catch (Exception ex)
                {
                    ok = false; msg = "异常：" + ex.Message;
                    Log.Error(ex, "TestCredential 异常");
                }
                BeginInvoke((Action)(() =>
                {
                    btn.Enabled = true;
                    if (ok) SetOk(_lblResult,    "✅ " + msg);
                    else    SetError(_lblResult, "❌ " + msg);
                }));
            });
        }

        private static (bool ok, string msg) TryLoginAndLogout(
            string ip, int port, string user, string pwd)
        {
            var login = new CHCNetSDK.NET_DVR_USER_LOGIN_INFO
            {
                sDeviceAddress = new byte[CHCNetSDK.NET_DVR_DEV_ADDRESS_MAX_LEN],
                sUserName      = new byte[CHCNetSDK.NET_DVR_LOGIN_USERNAME_MAX_LEN],
                sPassword      = new byte[CHCNetSDK.NET_DVR_LOGIN_PASSWD_MAX_LEN],
                wPort          = (ushort)port,
                bUseAsynLogin  = false,
                byRes3         = new byte[119]
            };
            WriteAnsi(login.sDeviceAddress, ip);
            WriteAnsi(login.sUserName, user);
            WriteAnsi(login.sPassword, pwd);

            var dev = new CHCNetSDK.NET_DVR_DEVICEINFO_V40
            {
                byRes2 = new byte[243]
            };

            int uid = CHCNetSDK.NET_DVR_Login_V40(ref login, ref dev);
            if (uid < 0)
            {
                uint err = CHCNetSDK.NET_DVR_GetLastError();
                return (false, $"登录失败，错误码 {err}");
            }

            try
            {
                string sn = ReadAnsi(dev.struDeviceV30.sSerialNumber);
                return (true, $"登录成功（通道 {dev.struDeviceV30.byStartChan}~{dev.struDeviceV30.byStartChan + dev.struDeviceV30.byChanNum - 1}，SN {sn}）");
            }
            finally
            {
                CHCNetSDK.NET_DVR_Logout(uid);
            }
        }

        private static void WriteAnsi(byte[] dst, string s)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(s ?? "");
            Array.Copy(bytes, dst, Math.Min(bytes.Length, dst.Length - 1));
        }

        private static string ReadAnsi(byte[] src)
        {
            if (src == null) return "";
            int len = Array.IndexOf(src, (byte)0);
            if (len < 0) len = src.Length;
            return System.Text.Encoding.ASCII.GetString(src, 0, len);
        }

        // ══════════════════════════════════════════════════════
        // 数据刷新
        // ══════════════════════════════════════════════════════

        public override void RefreshData()
        {
            _refreshing = true;

            // 设备下拉：只列启用中的洗车机
            int prevDev = _cmbDevice.SelectedItem is UIHelper.Item p ? p.Id : -1;
            _cmbDevice.Items.Clear();
            int devIdx = 0;
            foreach (var d in _devRepo.GetAll(enabledOnly: true).Where(x => x.DeviceType == "洗车机"))
            {
                int i = _cmbDevice.Items.Add(new UIHelper.Item(d.Id, $"{d.DeviceName} [{d.DeviceCode}]"));
                if (d.Id == prevDev) devIdx = i;
            }
            if (_cmbDevice.Items.Count > 0) _cmbDevice.SelectedIndex = devIdx;

            // 列表
            _grid.Rows.Clear();
            foreach (var c in _repo.GetAll())
            {
                var dev = _devRepo.GetById(c.DeviceId);
                string devName = dev != null ? $"{dev.DeviceName} [{dev.DeviceCode}]" : $"⚠ 设备 #{c.DeviceId} 不存在";
                int ri = _grid.Rows.Add(
                    c.Id, devName, c.Ip, c.Port, c.Channel, c.Username,
                    $"{c.MatchPreSec}/{c.MatchPostSec}s",
                    c.ImageStorePath,
                    c.Enabled == 1 ? "✔ 启用" : "✘ 禁用");
                if (c.Enabled == 0)
                    _grid.Rows[ri].DefaultCellStyle.ForeColor = Color.Gray;
                if (dev == null)
                    _grid.Rows[ri].Cells["Dev"].Style.ForeColor = Color.OrangeRed;
            }
            _refreshing = false;
        }

        private void SelectDeviceInCombo(int deviceId)
        {
            for (int i = 0; i < _cmbDevice.Items.Count; i++)
                if (_cmbDevice.Items[i] is UIHelper.Item it && it.Id == deviceId)
                { _cmbDevice.SelectedIndex = i; return; }
            _cmbDevice.SelectedIndex = -1;
        }

        private static decimal Clamp(int v, int lo, int hi) =>
            v < lo ? lo : (v > hi ? hi : v);

        private static void EnsureDirectory(string path)
        {
            string full = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            Directory.CreateDirectory(full);
        }
    }
}
