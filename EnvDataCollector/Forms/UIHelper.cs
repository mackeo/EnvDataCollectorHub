using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EnvDataCollector.Forms
{
    public interface IRefreshable { void RefreshData(); }

    /// <summary>
    /// UI 工厂与布局辅助。
    /// 所有 Panel 只从此处取控件，不允许在 Panel 内重复定义。
    /// </summary>
    public static class UIHelper
    {
        // ── 色板 ──────────────────────────────────────────────
        public static class C
        {
            public static readonly Color Primary = Color.FromArgb(41, 128, 185);
            public static readonly Color Success = Color.FromArgb(39, 174, 96);
            public static readonly Color Danger = Color.FromArgb(192, 57, 43);
            public static readonly Color Warning = Color.FromArgb(243, 156, 18);
            public static readonly Color Dark = Color.FromArgb(52, 73, 94);
            public static readonly Color Purple = Color.FromArgb(142, 68, 173);
            public static readonly Color Info = Color.FromArgb(52, 152, 219);
            public static readonly Color Surface = Color.FromArgb(250, 251, 252);
            public static readonly Color Border = Color.FromArgb(220, 220, 220);
            public static readonly Color TextMuted = Color.FromArgb(80, 80, 80);
            public static readonly Color HeaderBg = Color.FromArgb(236, 240, 241);
        }

        // ── 控件统一尺寸 ──────────────────────────────────────
        private const int RowH = 34;   // 表单行高
        private const int CtrlH = 26;   // 输入控件高度
        private const int VPad = (RowH - CtrlH) / 2;  // 垂直居中边距

        // ── Win32 PlaceholderText（.NET FW 4.8 无此属性）──────
        private const int EM_SETCUEBANNER = 0x1501;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void SetPlaceholder(this TextBox tb, string hint)
        {
            if (string.IsNullOrEmpty(hint)) return;
            void Send() => SendMessage(tb.Handle, EM_SETCUEBANNER, (IntPtr)1, hint);
            if (tb.IsHandleCreated) Send();
            else tb.HandleCreated += (s, e) => Send();
        }

        // ── 线程安全调用 ──────────────────────────────────────
        public static void SafeInvoke(Control ctrl, Action action)
        {
            if (ctrl == null || ctrl.IsDisposed) return;
            if (ctrl.InvokeRequired) ctrl.BeginInvoke(action);
            else action();
        }

        // ══════════════════════════════════════════════════════
        //  控件工厂（统一尺寸，杜绝各处 new 时不一致）
        // ══════════════════════════════════════════════════════

        public static TextBox MakeTextBox(string placeholder = null, bool password = false)
        {
            var tb = new TextBox();
            if (password) tb.UseSystemPasswordChar = true;
            if (!string.IsNullOrEmpty(placeholder)) tb.SetPlaceholder(placeholder);
            return tb;
        }

        public static ComboBox MakeCombo(params object[] items)
        {
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            if (items.Length > 0) { cb.Items.AddRange(items); cb.SelectedIndex = 0; }
            return cb;
        }

        public static NumericUpDown MakeNumeric(int min, int max, int value)
            => new NumericUpDown { Minimum = min, Maximum = max, Value = value };

        public static CheckBox MakeCheck(string text, bool isChecked = false)
            => new CheckBox { Text = text, Checked = isChecked, AutoSize = true };

        /// <summary>
        /// 工具栏用 DateTimePicker（年月日时分秒）。
        /// ★ Margin.Top=3 使文本基线与 InlineLabel/Button 对齐。
        /// </summary>
        public static DateTimePicker MakeDatePicker(DateTime? value = null, int width = 155)
            => new DateTimePicker
            {
                Format       = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd HH:mm:ss",
                Width        = width,
                Value        = value ?? DateTime.Today,
                Margin       = new Padding(0, 3, 4, 0)
            };

        /// <summary>工具栏用固定宽度 ComboBox（带 Margin.Top 对齐）</summary>
        public static ComboBox MakeToolbarCombo(int width, params object[] items)
        {
            var cb = MakeCombo(items);
            cb.Width  = width;
            cb.Margin = new Padding(0, 3, 6, 0);
            return cb;
        }

        /// <summary>工具栏用固定宽度 TextBox（带 Margin.Top 对齐）</summary>
        public static TextBox MakeToolbarTextBox(int width, string placeholder = null)
        {
            var tb = MakeTextBox(placeholder);
            tb.Width  = width;
            tb.Margin = new Padding(0, 3, 6, 0);
            return tb;
        }

        // ── 按钮 ──────────────────────────────────────────────
        public static Button MakeBtn(string text, Color? back = null) => new Button
        {
            Text = text,
            AutoSize = true,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = back ?? C.Primary,
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 6, 0)
        };

        // ── 标签 ──────────────────────────────────────────────

        /// <summary>表单行左侧标签：右对齐 + 右边距留 6px + 垂直居中</summary>
        public static Label FormLabel(string text) => new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = C.TextMuted,
            Padding = new Padding(0, 0, 6, 0)
        };

        /// <summary>工具栏内联说明标签</summary>
        public static Label InlineLabel(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 5, 2, 0)
        };

        /// <summary>结果/状态反馈标签</summary>
        public static Label ResultLabel() => new Label
        {
            AutoSize = true,
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0)
        };

        // ── 工具栏 ────────────────────────────────────────────
        public static Panel MakeToolbar(params Control[] controls)
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Padding = new Padding(0)
            };
            flow.Controls.AddRange(controls);
            var bar = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6, 6, 6, 0) };
            bar.Controls.Add(flow);
            return bar;
        }

        // ── 分隔线 ────────────────────────────────────────────
        public static Panel MakeSeparator() => new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = C.Border
        };

        // ── 区块标题条 ────────────────────────────────────────
        public static Panel MakeSectionHeader(string title) => new Panel
        {
            Dock = DockStyle.Top,
            Height = 26,
            BackColor = C.HeaderBg,
            Controls =
            {
                new Label
                {
                    Text      = "  " + title,
                    Dock      = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font      = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
                    ForeColor = C.TextMuted
                }
            }
        };

        // ══════════════════════════════════════════════════════
        //  2 列表单 （Label + Input）
        // ══════════════════════════════════════════════════════

        public static TableLayoutPanel MakeFormTable(int labelWidth = 110)
        {
            var tbl = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 0,
                AutoSize = true,
                Padding = new Padding(0)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return tbl;
        }

        /// <summary>
        /// 向 2 列表单追加一行。
        /// ★ 核心对齐逻辑：用 Anchor(左+右) + Margin 垂直居中，
        ///   不用 Dock.Fill（会拉伸控件高度导致文本偏上）。
        /// </summary>
        public static void AddRow(this TableLayoutPanel tbl, string label, Control ctrl, int height = 0)
        {
            if (height <= 0) height = RowH;
            int r = tbl.RowCount++;
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            ApplyRowAnchor(ctrl, height);
            tbl.Controls.Add(FormLabel(label), 0, r);
            tbl.Controls.Add(ctrl, 1, r);
        }

        /// <summary>控件横跨两列</summary>
        public static void AddRowSpan(this TableLayoutPanel tbl, Control ctrl, int height = 0)
        {
            if (height <= 0) height = RowH;
            int r = tbl.RowCount++;
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            ApplyRowAnchor(ctrl, height);
            tbl.Controls.Add(ctrl, 0, r);
            tbl.SetColumnSpan(ctrl, 2);
        }

        /// <summary>追加按钮行（FlowLayoutPanel 横跨两列）</summary>
        public static void AddBtnRow(this TableLayoutPanel tbl, params Control[] btns)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            flow.Controls.AddRange(btns);
            tbl.AddRowSpan(flow, 40);
        }

        // ══════════════════════════════════════════════════════
        //  4 列表单 （两对 Label+Input 并排）
        // ══════════════════════════════════════════════════════

        public static TableLayoutPanel MakeFormTable4(
            int lw1 = 75, int fw1 = 180, int lw2 = 90, int fw2 = -1)
        {
            var tbl = new TableLayoutPanel
            {
                ColumnCount = 4,
                RowCount = 0,
                Dock = DockStyle.Fill,
                Padding = new Padding(0)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, lw1));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, fw1));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, lw2));
            tbl.ColumnStyles.Add(fw2 > 0
                ? new ColumnStyle(SizeType.Absolute, fw2)
                : new ColumnStyle(SizeType.Percent, 100));
            return tbl;
        }

        public static void AddRow4(this TableLayoutPanel tbl,
            string lbl1, Control c1, string lbl2, Control c2, int height = 0)
        {
            if (height <= 0) height = RowH;
            int r = tbl.RowCount++;
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            ApplyRowAnchor(c1, height);
            ApplyRowAnchor(c2, height);
            tbl.Controls.Add(FormLabel(lbl1), 0, r);
            tbl.Controls.Add(c1, 1, r);
            tbl.Controls.Add(FormLabel(lbl2), 2, r);
            tbl.Controls.Add(c2, 3, r);
        }

        public static void AddRow4Span(this TableLayoutPanel tbl, Control ctrl, int height = 0)
        {
            if (height <= 0) height = RowH;
            int r = tbl.RowCount++;
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            ApplyRowAnchor(ctrl, height);
            tbl.Controls.Add(ctrl, 0, r);
            tbl.SetColumnSpan(ctrl, 4);
        }

        public static void AddBtnRow4(this TableLayoutPanel tbl, params Control[] btns)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            flow.Controls.AddRange(btns);
            tbl.AddRow4Span(flow, 40);
        }

        // ── 对齐核心：设置 Anchor + Margin 使控件在行内水平拉伸 + 垂直居中 ──
        private static void ApplyRowAnchor(Control ctrl, int rowHeight)
        {
            ctrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            int vp = Math.Max(0, (rowHeight - CtrlH) / 2);
            ctrl.Margin = new Padding(3, vp, 3, vp);
        }

        /// <summary>将 TableLayoutPanel 包在固定高度 Panel 里</summary>
        public static Panel WrapFormPanel(TableLayoutPanel tbl, int height, Padding? padding = null)
        {
            tbl.Dock = DockStyle.Fill;
            return new Panel
            {
                Dock = DockStyle.Top,
                Height = height,
                BackColor = C.Surface,
                Padding = padding ?? new Padding(12, 8, 12, 4),
                Controls = { tbl }
            };
        }

        // ══════════════════════════════════════════════════════
        //  仪表盘卡片
        // ══════════════════════════════════════════════════════

        /// <summary>仪表盘统计卡片（固定尺寸，标题+数值两行居中）</summary>
        public static Label MakeCard(string title, string value, Color color) => new Label
        {
            Text = $"{title}\n{value}",
            Width = 158,
            Height = 88,
            BackColor = color,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 8, 0)
        };

        // ══════════════════════════════════════════════════════
        //  带标题条的列面板（用于多栏布局）
        // ══════════════════════════════════════════════════════

        /// <summary>带 SectionHeader + 右侧竖线的列容器</summary>
        public static Panel ColPane(string title, Control content)
        {
            var border = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = C.Border };
            var p = new Panel { Dock = DockStyle.Fill };
            content.Dock = DockStyle.Fill;
            p.Controls.Add(content);
            p.Controls.Add(border);
            p.Controls.Add(MakeSectionHeader(title));
            return p;
        }

        /// <summary>创建三栏等分/自定义比例的 TableLayoutPanel</summary>
        public static TableLayoutPanel MakeThreeColBody(int pct1 = 28, int pct2 = 42, int pct3 = 30)
        {
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, pct1));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, pct2));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, pct3));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return body;
        }

        // ══════════════════════════════════════════════════════
        //  DataGridView
        // ══════════════════════════════════════════════════════

        public static DataGridView MakeGrid() => new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 9f),
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ColumnHeadersHeightSizeMode =
                DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 28
        };

        public static DataGridViewTextBoxColumn Col(
            string name, string header, int width = 100, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header };
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            else c.Width = width;
            return c;
        }

        /// <summary>按 Id 列值选中行并滚动可见</summary>
        public static void SelectRowById(this DataGridView grid, int id, string col = "Id")
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Cells[col].Value?.ToString() == id.ToString())
                {
                    row.Selected = true;
                    grid.FirstDisplayedScrollingRowIndex = row.Index;
                    break;
                }
        }

        // ── ComboBox 数据项 ──────────────────────────────────
        public class Item
        {
            public int Id { get; }
            public string Text { get; }
            public Item(int id, string text) { Id = id; Text = text; }
            public override string ToString() => Text;
        }
    }
}