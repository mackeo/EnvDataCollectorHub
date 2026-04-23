using System;
using System.Drawing;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;

namespace EnvDataCollector.Forms.Panels
{
    public class VariableBrowserPanel : PanelBase
    {
        private readonly MainForm                 _main;
        private readonly DeviceRepository         _devRepo = new();
        private readonly OpcUaServerRepository    _srvRepo = new();
        private readonly DeviceVariableRepository _varRepo = new();

        private ComboBox     _cmbDevice, _cmbServer;
        private TextBox      _txtSearch;
        private DataGridView _gridResult, _gridBound;
        private TreeView     _treeOpc;
        private Label        _lblStatus;
        private bool         _refreshing;

        public VariableBrowserPanel(MainForm main) { _main = main; BuildUI(); }

        private void BuildUI()
        {
            // ── 工具栏 ──────────────────────────────────────
            _lblStatus = UIHelper.ResultLabel();
            _cmbDevice = UIHelper.MakeToolbarCombo(195);
            _cmbServer = UIHelper.MakeToolbarCombo(155);
            _txtSearch = UIHelper.MakeToolbarTextBox(148, "关键字搜索变量");

            var btnBrowse  = UIHelper.MakeBtn("🌲 浏览",    UIHelper.C.Dark);
            var btnSearch  = UIHelper.MakeBtn("🔍 搜索",    UIHelper.C.Primary);
            var btnDelBind = UIHelper.MakeBtn("✂ 删除绑定", UIHelper.C.Danger);
            btnBrowse.Click  += (s, e) => Browse();
            btnSearch.Click  += (s, e) => Search();
            btnDelBind.Click += (s, e) => DeleteBinding();
            _cmbDevice.SelectedIndexChanged += (s, e) =>
            {
                if (_refreshing) return;
                SyncServerFromDevice(); LoadBound();
            };

            var toolbar = UIHelper.MakeToolbar(
                UIHelper.InlineLabel("设备:"), _cmbDevice,
                UIHelper.InlineLabel("Server:"), _cmbServer,
                _txtSearch, btnBrowse, btnSearch, btnDelBind, _lblStatus);

            // ── 主体三栏 ────────────────────────────────────
            _treeOpc = new TreeView
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            _treeOpc.NodeMouseDoubleClick += TreeNode_DoubleClick;
            _treeOpc.BeforeExpand         += Tree_BeforeExpand;

            _gridResult = UIHelper.MakeGrid();
            _gridResult.Columns.AddRange(
                UIHelper.Col("NodeId", "NodeId",  140),
                UIHelper.Col("Name",   "显示名",  120),
                UIHelper.Col("Path",   "路径",      0, true));
            _gridResult.MouseDoubleClick += (s, e) => ResultDoubleClick();

            _gridBound = UIHelper.MakeGrid();
            _gridBound.Columns.AddRange(
                UIHelper.Col("Role",   "角色",   90),
                UIHelper.Col("Name",   "显示名", 110),
                UIHelper.Col("NodeId", "NodeId",   0, true));

            var body = UIHelper.MakeThreeColBody(28, 42, 30);
            body.Controls.Add(UIHelper.ColPane("OPC UA 节点浏览",      _treeOpc),    0, 0);
            body.Controls.Add(UIHelper.ColPane("搜索结果（双击绑定）", _gridResult), 1, 0);
            body.Controls.Add(UIHelper.ColPane("已绑定变量",           _gridBound),  2, 0);

            Controls.Add(body);
            Controls.Add(toolbar);
        }

        // ── 浏览 ────────────────────────────────────────────
        private void Browse()
        {
            if (_cmbServer.SelectedItem is not UIHelper.Item srv)
            { SetError(_lblStatus, "请先选择 OPC UA Server"); return; }
            _treeOpc.Nodes.Clear();
            SetInfo(_lblStatus, "浏览中...");
            try
            {
                var nodes = _main.Opc.Browse(srv.Id);
                foreach (var n in nodes)
                {
                    var tn = new TreeNode(n.DisplayName) { Tag = n, Name = n.NodeId };
                    if (!n.IsVariable) tn.Nodes.Add(new TreeNode("...") { Name = "__lazy" });
                    _treeOpc.Nodes.Add(tn);
                }
                SetOk(_lblStatus, $"浏览完成，{nodes.Count} 个根节点");
            }
            catch (Exception ex) { SetError(_lblStatus, "浏览失败：" + ex.Message); }
        }

        private void Tree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Name != "__lazy") return;
            if (_cmbServer.SelectedItem is not UIHelper.Item srv) return;
            e.Node.Nodes.Clear();
            try
            {
                foreach (var c in _main.Opc.Browse(srv.Id, e.Node.Name))
                {
                    var cn = new TreeNode(c.DisplayName) { Tag = c, Name = c.NodeId };
                    if (!c.IsVariable) cn.Nodes.Add(new TreeNode("...") { Name = "__lazy" });
                    e.Node.Nodes.Add(cn);
                }
            }
            catch { }
        }

        private void TreeNode_DoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node?.Tag is OpcNodeInfo n && n.IsVariable) PromptBind(n.NodeId, n.DisplayName);
        }

        // ── 搜索 ────────────────────────────────────────────
        private void Search()
        {
            if (_cmbServer.SelectedItem is not UIHelper.Item srv)
            { SetError(_lblStatus, "请先选择 OPC UA Server"); return; }
            _gridResult.Rows.Clear();
            SetInfo(_lblStatus, "搜索中...");
            try
            {
                var list = _main.Opc.Search(srv.Id, _txtSearch.Text.Trim());
                foreach (var n in list) _gridResult.Rows.Add(n.NodeId, n.DisplayName, n.BrowsePath);
                SetOk(_lblStatus, $"搜索结果 {list.Count} 条");
            }
            catch (Exception ex) { SetError(_lblStatus, "搜索失败：" + ex.Message); }
        }

        private void ResultDoubleClick()
        {
            if (_gridResult.SelectedRows.Count == 0) return;
            string nid  = _gridResult.SelectedRows[0].Cells["NodeId"].Value?.ToString();
            string name = _gridResult.SelectedRows[0].Cells["Name"].Value?.ToString();
            if (!string.IsNullOrEmpty(nid)) PromptBind(nid, name);
        }

        // ── 绑定弹窗 ────────────────────────────────────────
        private void PromptBind(string nodeId, string name)
        {
            if (_cmbDevice.SelectedItem is not UIHelper.Item dev)
            { Tip("请先选择设备"); return; }

            using var dlg = new Form
            {
                Text = "选择绑定角色", Size = new Size(320, 190),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MaximizeBox = false
            };
            var info = new Label
            {
                Text = $"节点：{name}\n{nodeId}", Dock = DockStyle.Top,
                Height = 46, Padding = new Padding(8, 6, 8, 0),
                ForeColor = UIHelper.C.TextMuted
            };
            var cmb = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(8, 4, 8, 0) };
            foreach (VarRole r in Enum.GetValues(typeof(VarRole))) cmb.Items.Add(r.ToString());
            cmb.SelectedIndex = 0;

            var btnOk = UIHelper.MakeBtn("确定绑定");
            btnOk.DialogResult = DialogResult.OK;
            var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel,
                Height = 30, AutoSize = true };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44,
                Padding = new Padding(8) };
            flow.Controls.AddRange(new Control[] { btnOk, btnCancel });
            dlg.AcceptButton = btnOk; dlg.CancelButton = btnCancel;
            dlg.Controls.AddRange(new Control[] { flow, cmb, info });

            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string role = cmb.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(role)) { Tip("请选择角色"); return; }

            _varRepo.Upsert(new DeviceVariableEntity
                { DeviceId = dev.Id, VarRole = role, NodeId = nodeId, DisplayName = name, Enabled = 1 });
            LoadBound();
            SetOk(_lblStatus, $"✅ 已绑定 [{role}] → {name}");
        }

        private void DeleteBinding()
        {
            if (_gridBound.SelectedRows.Count == 0) { Tip("请先选择要删除的绑定行"); return; }
            if (_cmbDevice.SelectedItem is not UIHelper.Item dev) return;
            string role = _gridBound.SelectedRows[0].Cells["Role"].Value?.ToString();
            if (!Confirm($"确认删除角色「{role}」的绑定？")) return;
            var v = _varRepo.GetByRole(dev.Id, role);
            if (v != null) { v.Enabled = 0; _varRepo.Upsert(v); }
            LoadBound();
            SetError(_lblStatus, $"已移除 [{role}] 绑定");
        }

        private void LoadBound()
        {
            _gridBound.Rows.Clear();
            if (_cmbDevice.SelectedItem is not UIHelper.Item dev) return;
            foreach (var v in _varRepo.GetByDevice(dev.Id))
                if (v.Enabled == 1) _gridBound.Rows.Add(v.VarRole, v.DisplayName, v.NodeId);
        }

        private void SyncServerFromDevice()
        {
            if (_cmbDevice.SelectedItem is not UIHelper.Item dev) return;
            var d = _devRepo.GetById(dev.Id); if (d == null) return;
            for (int i = 0; i < _cmbServer.Items.Count; i++)
                if (_cmbServer.Items[i] is UIHelper.Item s && s.Id == d.ServerId)
                { _cmbServer.SelectedIndex = i; return; }
        }

        public override void RefreshData()
        {
            _refreshing = true;
            int pd = _cmbDevice.SelectedItem is UIHelper.Item d ? d.Id : -1;
            int ps = _cmbServer.SelectedItem is UIHelper.Item s ? s.Id : -1;
            _cmbDevice.Items.Clear(); _cmbServer.Items.Clear();
            int di = 0, si = 0;
            foreach (var dev in _devRepo.GetAll(true))
            {
                int i = _cmbDevice.Items.Add(new UIHelper.Item(dev.Id, $"{dev.DeviceCode}  {dev.DeviceName}"));
                if (dev.Id == pd) di = i;
            }
            foreach (var srv in _srvRepo.GetAll())
            {
                int i = _cmbServer.Items.Add(new UIHelper.Item(srv.Id, srv.Name));
                if (srv.Id == ps) si = i;
            }
            if (_cmbDevice.Items.Count > 0) _cmbDevice.SelectedIndex = di;
            if (_cmbServer.Items.Count > 0) _cmbServer.SelectedIndex = si;
            _refreshing = false;
            SyncServerFromDevice(); LoadBound();
        }
    }
}
