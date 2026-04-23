using System;
using System.Drawing;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using EnvDataCollector.Services;

namespace EnvDataCollector.Forms.Panels
{
    public class OpcUaConfigPanel : PanelBase
    {
        private readonly MainForm              _main;
        private readonly OpcUaServerRepository _repo = new();

        private TextBox  _txtName, _txtUrl, _txtUser, _txtPwd;
        private ComboBox _cmbSecMode, _cmbAuth;
        private DataGridView _grid;
        private Label    _lblResult;
        private Button   _btnToggle;

        private int  _editId      = -1;
        private bool _editEnabled = true;

        public OpcUaConfigPanel(MainForm main) { _main = main; BuildUI(); }

        private void BuildUI()
        {
            // ── 工具栏 ──────────────────────────────────────
            _lblResult    = UIHelper.ResultLabel();
            var btnNew    = UIHelper.MakeBtn("➕ 新建",     UIHelper.C.Dark);
            var btnSave   = UIHelper.MakeBtn("💾 保存",     UIHelper.C.Primary);
            var btnDel    = UIHelper.MakeBtn("🗑 删除",     UIHelper.C.Danger);
            _btnToggle    = UIHelper.MakeBtn("⏸ 禁用",     UIHelper.C.Warning);
            var btnReconn = UIHelper.MakeBtn("🔄 断开重连", UIHelper.C.Success);
            var btnTest   = UIHelper.MakeBtn("🔌 测试连接", UIHelper.C.Purple);
            var btnRefresh= UIHelper.MakeBtn("🔃 刷新");

            btnNew.Click       += (s, e) => NewRecord();
            btnSave.Click      += (s, e) => Save();
            btnDel.Click       += (s, e) => Delete();
            _btnToggle.Click   += (s, e) => ToggleEnabled();
            btnReconn.Click    += (s, e) => Reconnect();
            btnTest.Click      += async (s, e) => await TestConnect();
            btnRefresh.Click   += (s, e) => { RefreshData(); SetInfo(_lblResult, $"已刷新 @ {DateTime.Now:HH:mm:ss}"); };

            var toolbar = UIHelper.MakeToolbar(
                btnNew, btnSave, btnDel, _btnToggle, btnReconn, btnTest, btnRefresh, _lblResult);

            // ── 表单（4列） ─────────────────────────────────
            _txtName    = UIHelper.MakeTextBox("服务器名称");
            _txtUrl     = UIHelper.MakeTextBox("opc.tcp://192.168.1.1:4840");
            _txtUser    = UIHelper.MakeTextBox("用户名（匿名可留空）");
            _txtPwd     = UIHelper.MakeTextBox(password: true);
            _cmbSecMode = UIHelper.MakeCombo("None", "Sign", "SignAndEncrypt");
            _cmbAuth    = UIHelper.MakeCombo("Anonymous", "UsernamePassword");

            var tbl = UIHelper.MakeFormTable4(lw1: 75, fw1: 180, lw2: 95);
            tbl.AddRow4("名称",     _txtName,    "EndpointUrl", _txtUrl);
            tbl.AddRow4("安全模式", _cmbSecMode, "认证方式",    _cmbAuth);
            tbl.AddRow4("用户名",   _txtUser,    "密码",        _txtPwd);

            var formPanel = UIHelper.WrapFormPanel(tbl, height: 114);

            // ── 列表 ────────────────────────────────────────
            _grid = UIHelper.MakeGrid();
            _grid.Columns.AddRange(
                UIHelper.Col("Id",   "ID",          45),
                UIHelper.Col("Name", "名称",        120),
                UIHelper.Col("Url",  "EndpointUrl", 280),
                UIHelper.Col("Auth", "认证方式",    110),
                UIHelper.Col("Conn", "连接状态",     80),
                UIHelper.Col("Ena",  "启用",          0, true));
            _grid.SelectionChanged += (s, e) => LoadRow();

            Controls.Add(_grid);
            Controls.Add(UIHelper.MakeSeparator());
            Controls.Add(formPanel);
            Controls.Add(toolbar);
        }

        private void LoadRow()
        {
            if (_grid.SelectedRows.Count == 0) return;
            if (!int.TryParse(_grid.SelectedRows[0].Cells["Id"].Value?.ToString(), out int id)) return;
            _editId = id;
            var e = _repo.GetById(id); if (e == null) return;
            _txtName.Text = e.Name;
            _txtUrl.Text  = e.EndpointUrl;
            _cmbSecMode.SelectedIndex = Math.Max(0, _cmbSecMode.FindStringExact(e.SecurityMode));
            _cmbAuth.SelectedIndex    = Math.Max(0, _cmbAuth.FindStringExact(e.AuthType));
            _txtUser.Text = e.Username ?? "";
            _txtPwd.Text  = CryptoHelper.Decrypt(e.PasswordEnc);
            _editEnabled  = e.Enabled == 1;
            SyncToggleBtn(_btnToggle, _editEnabled);
            SetInfo(_lblResult, "");
        }

        private void NewRecord()
        {
            _editId = -1;
            _txtName.Text = _txtUrl.Text = _txtUser.Text = _txtPwd.Text = "";
            _cmbSecMode.SelectedIndex = _cmbAuth.SelectedIndex = 0;
            _editEnabled = true;
            SyncToggleBtn(_btnToggle, _editEnabled);
            SetInfo(_lblResult, "");
            _grid.ClearSelection();
            _txtName.Focus();
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_txtName.Text.Trim())) { Tip("服务器名称不能为空"); return; }
            if (string.IsNullOrEmpty(_txtUrl.Text.Trim()))  { Tip("EndpointUrl 不能为空"); return; }

            var entity = new OpcUaServerEntity
            {
                Id           = _editId > 0 ? _editId : 0,
                Name         = _txtName.Text.Trim(),
                EndpointUrl  = _txtUrl.Text.Trim(),
                SecurityMode = _cmbSecMode.SelectedItem?.ToString() ?? "None",
                AuthType     = _cmbAuth.SelectedItem?.ToString()    ?? "Anonymous",
                Username     = _txtUser.Text.Trim(),
                PasswordEnc  = CryptoHelper.Encrypt(_txtPwd.Text),
                Enabled      = 1
            };
            if (_editId > 0)
            {
                _repo.Update(entity);
                _main.Opc.ForceReconnect(_editId);
                SetOk(_lblResult, "✅ 已保存，正在重连...");
            }
            else
            {
                _editId = _repo.Insert(entity);
                SetOk(_lblResult, "✅ 新建成功");
            }
            RefreshData();
            _grid.SelectRowById(_editId);
        }

        private void Delete()
        {
            if (_editId <= 0) { Tip("请先选择一条记录"); return; }
            if (!Confirm($"确认删除「{_txtName.Text}」？")) return;
            _repo.Delete(_editId);
            _editId = -1; NewRecord(); RefreshData();
            SetError(_lblResult, "已删除");
        }

        private void ToggleEnabled()
        {
            if (_editId <= 0) { Tip("请先选择一条记录"); return; }
            var e = _repo.GetById(_editId); if (e == null) return;
            e.Enabled    = e.Enabled == 1 ? 0 : 1;
            _repo.Update(e);
            _editEnabled = e.Enabled == 1;
            SyncToggleBtn(_btnToggle, _editEnabled);
            SetResult(_lblResult,
                _editEnabled ? "✅ 已启用" : "⏸ 已禁用",
                _editEnabled ? UIHelper.C.Success : Color.OrangeRed);
            RefreshData();
            _grid.SelectRowById(_editId);
        }

        private void Reconnect()
        {
            if (_editId <= 0) { Tip("请先选择一条记录"); return; }
            _main.Opc.ForceReconnect(_editId);
            SetInfo(_lblResult, "🔄 已发送重连信号...");
        }

        private async System.Threading.Tasks.Task TestConnect()
        {
            SetInfo(_lblResult, "测试中...");
            bool ok = await System.Threading.Tasks.Task.Run(() => _main.Opc.IsConnected(_editId));
            UIHelper.SafeInvoke(this, () =>
                SetResult(_lblResult,
                    ok ? "✅ 当前已连接" : "❌ 未连接（保存后等待自动重连）",
                    ok ? UIHelper.C.Success : Color.OrangeRed));
        }

        public override void RefreshData()
        {
            int keep = _editId;
            _grid.Rows.Clear();
            foreach (var s in _repo.GetAll())
            {
                bool conn = _main.Opc.IsConnected(s.Id);
                int  idx  = _grid.Rows.Add(s.Id, s.Name, s.EndpointUrl, s.AuthType,
                    conn ? "● 已连接" : "○ 断开", s.Enabled == 1 ? "✔ 启用" : "✘ 禁用");
                var row = _grid.Rows[idx];
                row.DefaultCellStyle.ForeColor = conn ? UIHelper.C.Success : Color.OrangeRed;
                if (s.Enabled == 0) row.DefaultCellStyle.BackColor = Color.FromArgb(250, 245, 245);
            }
            if (keep > 0) _grid.SelectRowById(keep);
        }
    }
}
