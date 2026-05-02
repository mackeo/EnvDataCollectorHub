using System;
using System.Drawing;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;

namespace EnvDataCollector.Forms.Panels
{
    public class DeviceManagePanel : PanelBase
    {
        private readonly MainForm              _main;
        private readonly DeviceRepository      _repo    = new();
        private readonly OpcUaServerRepository _srvRepo = new();

        private TextBox  _txtCode, _txtName;
        private ComboBox _cmbType, _cmbSrv;
        private CheckBox _chkEnabled;
        private DataGridView _grid;
        private Label    _lblResult;
        private Button   _btnToggle;

        private int  _editId      = -1;
        private bool _editEnabled = true;
        private bool _refreshing;

        public DeviceManagePanel(MainForm main) { _main = main; BuildUI(); }

        private void BuildUI()
        {
            // ── 工具栏 ──────────────────────────────────────
            _lblResult = UIHelper.ResultLabel();
            var btnNew = UIHelper.MakeBtn("➕ 新建",  UIHelper.C.Dark);
            var btnSave= UIHelper.MakeBtn("💾 保存",  UIHelper.C.Primary);
            var btnDel = UIHelper.MakeBtn("🗑 删除",  UIHelper.C.Danger);
            _btnToggle = UIHelper.MakeBtn("⏸ 禁用",  UIHelper.C.Warning);
            var btnUnbind = UIHelper.MakeBtn("🔗 解绑 Server", UIHelper.C.Purple);
            btnNew.Click     += (s, e) => NewRecord();
            btnSave.Click    += (s, e) => Save();
            btnDel.Click     += (s, e) => Delete();
            _btnToggle.Click += (s, e) => ToggleEnabled();
            btnUnbind.Click  += (s, e) => UnbindServer();
            var toolbar = UIHelper.MakeToolbar(btnNew, btnSave, btnDel, _btnToggle, btnUnbind, _lblResult);

            // ── 表单（4列） ─────────────────────────────────
            _txtCode    = UIHelper.MakeTextBox("设备唯一编码");
            _txtName    = UIHelper.MakeTextBox("设备名称");
            _txtCode.MaxLength = 20;   // 平台接口字段长度上限
            _txtName.MaxLength = 20;
            _cmbType    = UIHelper.MakeCombo("洗车机", "雾炮", "干雾除尘");
            _cmbSrv     = UIHelper.MakeCombo();
            _chkEnabled = UIHelper.MakeCheck("启用此设备", true);

            var tbl = UIHelper.MakeFormTable4(lw1: 75, fw1: 180, lw2: 95);
            tbl.AddRow4("设备编码",     _txtCode, "设备名称",      _txtName);
            tbl.AddRow4("设备类型",     _cmbType, "OPC UA 服务器", _cmbSrv);
            tbl.AddRow4Span(_chkEnabled, height: 30);

            var formPanel = UIHelper.WrapFormPanel(tbl, height: 112);

            // ── 列表 ────────────────────────────────────────
            _grid = UIHelper.MakeGrid();
            _grid.Columns.AddRange(
                UIHelper.Col("Id",   "ID",           45),
                UIHelper.Col("Type", "类型",          70),
                UIHelper.Col("Code", "设备编码",     120),
                UIHelper.Col("Name", "设备名称",     150),
                UIHelper.Col("Srv",  "OPC UA 服务器",160),
                UIHelper.Col("Ena",  "状态",           0, true));
            _grid.SelectionChanged += (s, e) => { if (!_refreshing) LoadRow(); };

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
            var d = _repo.GetById(id); if (d == null) return;
            _txtCode.Text          = d.DeviceCode;
            _txtName.Text          = d.DeviceName;
            _cmbType.SelectedIndex = Math.Max(0, _cmbType.FindStringExact(d.DeviceType));
            _cmbSrv.SelectedIndex = -1;
            if (d.ServerId > 0)
                for (int i = 0; i < _cmbSrv.Items.Count; i++)
                    if (_cmbSrv.Items[i] is UIHelper.Item it && it.Id == d.ServerId)
                    { _cmbSrv.SelectedIndex = i; break; }
            _chkEnabled.Checked = _editEnabled = d.Enabled == 1;
            SyncToggleBtn(_btnToggle, _editEnabled);
            if (d.ServerId > 0 && _cmbSrv.SelectedIndex < 0)
                SetError(_lblResult, $"⚠ 原绑定的 OPC UA Server #{d.ServerId} 已不存在，请重新选择");
            else
                SetInfo(_lblResult, "");
        }

        private void NewRecord()
        {
            _editId = -1;
            _txtCode.Text = _txtName.Text = "";
            _cmbType.SelectedIndex = 0;
            if (_cmbSrv.Items.Count > 0) _cmbSrv.SelectedIndex = 0;
            else                          _cmbSrv.SelectedIndex = -1;
            _chkEnabled.Checked = _editEnabled = true;
            SyncToggleBtn(_btnToggle, _editEnabled);
            _grid.ClearSelection();
            if (_cmbSrv.Items.Count == 0)
                SetError(_lblResult, "⚠ 尚无 OPC UA Server，请先到「OPC UA 数据源」页面添加");
            else
                SetInfo(_lblResult, "");
            _txtCode.Focus();
        }

        private void Save()
        {
            string code = _txtCode.Text.Trim();
            string name = _txtName.Text.Trim();
            string type = _cmbType.SelectedItem?.ToString();

            if (string.IsNullOrEmpty(type))                      { Tip("请选择设备类型");      return; }
            if (string.IsNullOrEmpty(code))                      { Tip("设备编码不能为空");    return; }
            if (string.IsNullOrEmpty(name))                      { Tip("设备名称不能为空");    return; }
            if (code.Length > 20)                                { Tip("设备编码长度不能超过 20"); return; }
            if (name.Length > 20)                                { Tip("设备名称长度不能超过 20"); return; }
            if (_cmbSrv.SelectedItem is not UIHelper.Item srv)   { Tip("请选择 OPC UA 服务器"); return; }

            // 唯一性预检：避免触发 SQLite UNIQUE 约束异常导致界面崩溃
            var dup = _repo.GetByCode(code);
            if (dup != null && dup.Id != _editId)
            {
                Tip($"设备编码「{code}」已被设备「{dup.DeviceName}」(ID={dup.Id}) 占用");
                return;
            }

            var entity = new DeviceEntity
            {
                Id         = _editId > 0 ? _editId : 0,
                DeviceType = type,
                DeviceCode = code,
                DeviceName = name,
                ServerId   = srv.Id,
                Enabled    = _chkEnabled.Checked ? 1 : 0
            };

            try
            {
                if (_editId > 0) _repo.Update(entity);
                else             _editId = _repo.Insert(entity);
            }
            catch (Exception ex)
            {
                SetError(_lblResult, "❌ 保存失败：" + ex.Message);
                return;
            }

            _editEnabled = entity.Enabled == 1;
            SyncToggleBtn(_btnToggle, _editEnabled);
            RefreshData();
            _grid.SelectRowById(_editId);
            SetOk(_lblResult, _editEnabled ? "✅ 已保存" : "✅ 已保存（当前为禁用状态）");
        }

        private void Delete()
        {
            if (_grid.SelectedRows.Count == 0) { Tip("请先选择一条记录"); return; }
            if (!int.TryParse(_grid.SelectedRows[0].Cells["Id"].Value?.ToString(), out int id)) { Tip("无法获取设备ID"); return; }

            int varCnt = _repo.CountVariables(id);
            int camCnt = _repo.CountCameras(id);
            string code = _grid.SelectedRows[0].Cells["Code"].Value?.ToString() ?? "";
            string extra = (varCnt + camCnt) > 0
                ? $"\n将同时删除：{varCnt} 条变量绑定、{camCnt} 条摄像头配置。"
                : "";
            if (!Confirm($"确认删除设备「{code}」？{extra}\n（历史运行记录与状态快照会保留作为审计留痕）")) return;

            try
            {
                _repo.DeleteCascade(id);
            }
            catch (Exception ex)
            {
                SetError(_lblResult, "❌ 删除失败：" + ex.Message);
                return;
            }

            _editId = -1;
            RefreshData();
            NewRecord();
            SetError(_lblResult, "🗑 已删除");
        }

        private void ToggleEnabled()
        {
            if (_grid.SelectedRows.Count == 0) { Tip("请先选择一条记录"); return; }
            if (!int.TryParse(_grid.SelectedRows[0].Cells["Id"].Value?.ToString(), out int id)) { Tip("无法获取设备ID"); return; }
            var d = _repo.GetById(id); if (d == null) return;
            d.Enabled = d.Enabled == 1 ? 0 : 1;
            _repo.Update(d);
            _chkEnabled.Checked = _editEnabled = d.Enabled == 1;
            _editId = id;
            SyncToggleBtn(_btnToggle, _editEnabled);
            SetResult(_lblResult,
                _editEnabled ? "✅ 已启用" : "⏸ 已禁用",
                _editEnabled ? UIHelper.C.Success : Color.OrangeRed);
            RefreshData();
            _grid.SelectRowById(id);
        }

        private void UnbindServer()
        {
            if (_grid.SelectedRows.Count == 0) { Tip("请先选中要解绑的设备行（支持多选）"); return; }
            int cnt = _grid.SelectedRows.Count;
            if (!Confirm($"确认将选中的 {cnt} 台设备解绑 OPC UA Server？\n解绑后设备不会采集数据，直到重新绑定。")) return;

            int done = 0;
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (!int.TryParse(row.Cells["Id"].Value?.ToString(), out int id)) continue;
                var d = _repo.GetById(id); if (d == null) continue;
                d.ServerId = 0;
                d.Enabled = 0;
                _repo.Update(d);
                done++;
            }
            RefreshData();
            SetResult(_lblResult, $"✅ 已解绑 {done} 台设备", UIHelper.C.Success);
        }

        public override void RefreshData()
        {
            _refreshing = true;
            int prevSrv = _cmbSrv.SelectedItem is UIHelper.Item p ? p.Id : -1;
            _cmbSrv.Items.Clear();
            int srvIdx = 0;
            foreach (var s in _srvRepo.GetAll())
            {
                int i = _cmbSrv.Items.Add(new UIHelper.Item(s.Id, s.Name));
                if (s.Id == prevSrv) srvIdx = i;
            }
            if (_cmbSrv.Items.Count > 0) _cmbSrv.SelectedIndex = srvIdx;

            _grid.Rows.Clear();
            foreach (var d in _repo.GetAll())
            {
                string srvName;
                if (d.ServerId <= 0)
                    srvName = "⚠ 未绑定";
                else
                {
                    var srv = _srvRepo.GetById(d.ServerId);
                    srvName = srv?.Name ?? $"Server#{d.ServerId}";
                }
                int ri = _grid.Rows.Add(d.Id, d.DeviceType, d.DeviceCode, d.DeviceName,
                    srvName, d.Enabled == 1 ? "✔ 启用" : "✘ 禁用");
                if (d.Enabled == 0)
                    _grid.Rows[ri].DefaultCellStyle.ForeColor = Color.Gray;
                else if (d.ServerId <= 0)
                    _grid.Rows[ri].Cells["Srv"].Style.ForeColor = Color.OrangeRed;
            }
            _refreshing = false;
        }
    }
}