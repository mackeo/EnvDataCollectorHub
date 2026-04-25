using System.Drawing;
using System.Windows.Forms;

namespace EnvDataCollector.Forms
{
    partial class MainForm
    {
        private TableLayoutPanel _mainLayout;
        private TreeView _navTree;
        private Panel _contentPanel;
        private StatusStrip _status;
        private ToolStripStatusLabel _lblOpc, _lblCam, _lblPush, _lblPending, _lblTime;
        private Label _lblTitle;

        // 记录当前选中节点，焦点转移后也能保持高亮
        private TreeNode _selectedNode;

        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.Size = new Size(1200, 760);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Microsoft YaHei UI", 9f);
            this.BackColor = Color.FromArgb(245, 247, 250);

            // ── 状态栏
            _status = new StatusStrip { BackColor = Color.FromArgb(44, 62, 80), SizingGrip = false };
            Color slc = Color.FromArgb(189, 195, 199);
            _lblOpc = new ToolStripStatusLabel("OpcUA ✓") { ForeColor = slc, BorderSides = ToolStripStatusLabelBorderSides.Right };
            _lblCam = new ToolStripStatusLabel("摄像头 ✓") { ForeColor = slc, BorderSides = ToolStripStatusLabelBorderSides.Right };
            _lblPush = new ToolStripStatusLabel("推送 ✓") { ForeColor = slc, BorderSides = ToolStripStatusLabelBorderSides.Right };
            _lblPending = new ToolStripStatusLabel("积压 0") { ForeColor = slc, BorderSides = ToolStripStatusLabelBorderSides.Right };
            _lblTime = new ToolStripStatusLabel("--:--:--") { ForeColor = slc, Alignment = ToolStripItemAlignment.Right };
            _status.Items.AddRange(new ToolStripItem[] { _lblOpc, _lblCam, _lblPush, _lblPending, _lblTime });

            // ── 顶部标题
            _lblTitle = new Label
            {
                Text = "  原料大棚洗车与除尘数据采集系统",
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = Color.FromArgb(44, 62, 80),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // ── 主体：TableLayoutPanel 左列固定155px，右列填满
            _mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.FromArgb(52, 73, 94),
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            _mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
            _mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // ── 左侧导航 TreeView
            _navTree = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(52, 73, 94),
                ForeColor = Color.FromArgb(189, 195, 199),
                Font = new Font("Microsoft YaHei UI", 9.5f),
                BorderStyle = BorderStyle.None,
                ItemHeight = 36,
                Indent = 16,
                ShowLines = false,
                ShowPlusMinus = false,
                ShowRootLines = false,
                Margin = new Padding(0),
                // 关键1：禁用系统默认的失焦变灰行为
                HideSelection = false,
                // 关键2：完全接管绘制，系统不再参与任何节点背景
                DrawMode = TreeViewDrawMode.OwnerDrawAll
            };
            BuildNavTree();
            _navTree.DrawNode += NavTree_DrawNode;
            // 记录选中节点，失焦后触发重绘以维持高亮
            _navTree.AfterSelect += (s, e) =>
            {
                _selectedNode = e.Node;
                _navTree.Invalidate(); // 刷新整棵树确保旧节点取消高亮
            };
            _navTree.LostFocus += (s, e) => _navTree.Invalidate();
            _navTree.GotFocus += (s, e) => _navTree.Invalidate();

            // ── 右侧内容区
            _contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(245, 247, 250),
                Padding = new Padding(8),
                Margin = new Padding(0)
            };

            _mainLayout.Controls.Add(_navTree, 0, 0);
            _mainLayout.Controls.Add(_contentPanel, 1, 0);

            this.Controls.Add(_mainLayout);
            this.Controls.Add(_lblTitle);
            this.Controls.Add(_status);
            this.ResumeLayout();
        }

        private void BuildNavTree()
        {
            void Add(string key, string text, string parent = null)
            {
                var node = new TreeNode(text) { Name = key };
                if (parent == null) _navTree.Nodes.Add(node);
                else _navTree.Nodes[parent]?.Nodes.Add(node);
            }

            Add("Dashboard", "📊 总览仪表盘");
            Add("Config", "⚙ 配置管理");
            Add("OpcUaConfig", " OPC UA 数据源", "Config");
            Add("DeviceManage", " 设备管理", "Config");
            Add("VarBrowser", " 变量浏览/绑定", "Config");
            Add("CameraConfig", " 摄像头与车牌", "Config");
            Add("PlatformApi", " 平台接口/鉴权", "Config");
            Add("Data", "📋 数据管理");
            Add("PlateEvent", " 车牌事件浏览", "Data");
            Add("Outbox", " 推送队列补传", "Data");
            Add("RunRecord", " 运行记录查询", "Data");
            Add("Cleanup", " 数据清理维护", "Data");
            Add("Modbus", "🔌 Modbus反馈");
            Add("Log", "🗒 日志与诊断");

            _navTree.ExpandAll();
        }

        private void NavTree_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            // 记录_selectedNode 判断是否选中不依赖 e.State，避免失焦后 Selected 标志被系统清除
            bool isSelected = e.Node == _selectedNode;

            bool isGroup = e.Node.Name == "Config" || e.Node.Name == "Data";

            Color bg = isSelected
                ? Color.FromArgb(52, 152, 219)          // 选中：蓝色
                : isGroup
                    ? Color.FromArgb(40, 57, 72)         // 分组：深色
                    : Color.FromArgb(52, 73, 94);        // 普通：导航背景色

            Color fg = isSelected
                ? Color.White
                : isGroup
                    ? Color.FromArgb(149, 165, 166)      // 分组：灰色文字
                    : Color.FromArgb(236, 240, 241);     // 普通：亮白文字

            // 填充整行背景（必须覆盖到控件右边缘，否则右侧留白）
            var fullRowRect = new Rectangle(0, e.Bounds.Top, _navTree.Width, e.Bounds.Height);
            e.Graphics.FillRectangle(new SolidBrush(bg), fullRowRect);

            // 选中时左侧画一条竖线指示条
            if (isSelected)
                e.Graphics.FillRectangle(
                    new SolidBrush(Color.FromArgb(41, 182, 246)),
                    new Rectangle(0, e.Bounds.Top, 3, e.Bounds.Height));

            // 文字：缩进与节点层级对齐
            int textX = e.Node.Level == 0 ? 10 : 20;
            TextRenderer.DrawText(
                e.Graphics,
                e.Node.Text,
                _navTree.Font,
                new Point(textX, e.Bounds.Top + (e.Bounds.Height - 16) / 2),
                fg,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }
}