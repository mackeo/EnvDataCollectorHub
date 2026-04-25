using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Dapper;
using EnvDataCollector.Data;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using NLog;

namespace EnvDataCollector.Forms.Panels
{
    public class RunRecordPanel : PanelBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        private readonly MainForm              _main;
        private readonly RunRecordRepository   _repo = new();

        private DataGridView   _grid;
        private DateTimePicker _dtFrom, _dtTo;
        private TextBox        _txtCode, _txtPlate;
        private ComboBox       _cmbStatus;
        private Label          _lblResult;
        private List<RunRecordEntity> _items = new();

        public RunRecordPanel(MainForm main) { _main = main; BuildUI(); }

        // ══════════════════════════════════════════════════════
        // UI
        // ══════════════════════════════════════════════════════

        private void BuildUI()
        {
            _dtFrom    = UIHelper.MakeDatePicker(DateTime.Today);
            _dtTo      = UIHelper.MakeDatePicker(DateTime.Today.AddDays(1).AddSeconds(-1));
            _txtCode   = UIHelper.MakeToolbarTextBox(100, "设备编码");
            _txtPlate  = UIHelper.MakeToolbarTextBox(100, "车牌");
            _cmbStatus = UIHelper.MakeToolbarCombo(78, "全部", "Pending", "Failed", "Success");
            _lblResult = UIHelper.ResultLabel();

            var btnQuery   = UIHelper.MakeBtn("🔍 查询",   UIHelper.C.Primary);
            var btnScan    = UIHelper.MakeBtn("▶ 立即扫描", UIHelper.C.Dark);
            var btnRematch = UIHelper.MakeBtn("🔄 重跑车牌匹配", UIHelper.C.Info);
            var btnResend  = UIHelper.MakeBtn("⚡ 重发选中",  UIHelper.C.Success);
            var btnAdmin   = UIHelper.MakeBtn("✅ 管理员标记成功", UIHelper.C.Purple);

            btnQuery.Click   += (s, e) => RefreshData();
            btnScan.Click    += (s, e) => ScanNow();
            btnRematch.Click += (s, e) => RematchSelected();
            btnResend.Click  += (s, e) => ResendSelected();
            btnAdmin.Click   += (s, e) => AdminMarkSuccess();

            var toolbar = UIHelper.MakeToolbar(
                UIHelper.InlineLabel("从:"), _dtFrom,
                UIHelper.InlineLabel("至:"), _dtTo,
                _txtCode, _txtPlate, _cmbStatus,
                btnQuery, btnScan, btnRematch, btnResend, btnAdmin, _lblResult);

            _grid = UIHelper.MakeGrid();
            _grid.Columns.AddRange(
                UIHelper.Col("Id",     "ID",        50),
                UIHelper.Col("Code",   "设备编码", 100),
                UIHelper.Col("Type",   "类型",      70),
                UIHelper.Col("Start",  "开始时间", 145),
                UIHelper.Col("End",    "结束时间", 145),
                UIHelper.Col("Sec",    "秒",         55),
                UIHelper.Col("Plate",  "车牌",       90),
                UIHelper.Col("Curr",   "电流末值",   75),
                UIHelper.Col("Press",  "水压末值",   75),
                UIHelper.Col("Flow",   "流量末值",   75),
                UIHelper.Col("Reason", "关闭原因",   90),
                UIHelper.Col("Push",   "推送",        0, true));
            _grid.CellDoubleClick += (s, e) => OpenDetail(e.RowIndex);

            Controls.Add(_grid);
            Controls.Add(UIHelper.MakeSeparator());
            Controls.Add(toolbar);
        }

        // ══════════════════════════════════════════════════════
        // 数据加载
        // ══════════════════════════════════════════════════════

        public override void RefreshData()
        {
            DateTime from = _dtFrom.Value, to = _dtTo.Value;
            if (to < from) (from, to) = (to, from);
            string status = _cmbStatus.SelectedItem?.ToString();
            if (status == "全部") status = null;
            string code  = string.IsNullOrWhiteSpace(_txtCode.Text)  ? null : _txtCode.Text.Trim();
            string plate = string.IsNullOrWhiteSpace(_txtPlate.Text) ? null : _txtPlate.Text.Trim();

            try
            {
                _items = _repo.Query(from, to, code, status, plate).ToList();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "查询 run_record 失败");
                SetError(_lblResult, "❌ 查询失败：" + ex.Message);
                return;
            }

            _grid.Rows.Clear();
            foreach (var r in _items)
            {
                _grid.Rows.Add(
                    r.Id, r.DeviceCode, r.DeviceType,
                    r.StartTime, r.EndTime, r.RunTimeSec,
                    r.VehicleNo,
                    Fmt(r.Currents), Fmt(r.WaterPressure), Fmt(r.FlowQuantity),
                    r.CloseReason, r.PushStatus);
            }
            SetOk(_lblResult, $"✅ 共 {_items.Count} 条" + (_items.Count >= 500 ? "（已截断到 500 条）" : ""));
        }

        private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("F2") : "-";

        // ══════════════════════════════════════════════════════
        // 立即扫描 / 重跑车牌匹配 / 重发 / 管理员标记
        // ══════════════════════════════════════════════════════

        private void ScanNow()
        {
            if (_main?.RunBuilder == null) { Tip("RunRecordBuilder 未初始化"); return; }
            try
            {
                int n = _main.RunBuilder.ScanOnce();
                if (n > 0)
                {
                    SetOk(_lblResult, $"✅ 扫描完成，新增 {n} 条记录");
                    RefreshData();
                }
                else
                {
                    SetInfo(_lblResult, "ℹ️ 扫描完成，无新增");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ScanNow 失败");
                SetError(_lblResult, "❌ 扫描失败：" + ex.Message);
            }
        }

        private void RematchSelected()
        {
            if (_grid.SelectedRows.Count == 0) { Tip("请先选中一行或多行"); return; }
            if (_main?.RunBuilder == null)     { Tip("RunRecordBuilder 未初始化"); return; }

            int ok = 0, miss = 0;
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (!long.TryParse(row.Cells["Id"].Value?.ToString(), out long id)) continue;
                try
                {
                    if (_main.RunBuilder.RematchPlate(id)) ok++; else miss++;
                }
                catch (Exception ex) { Log.Warn(ex, "RematchPlate id={0}", id); miss++; }
            }
            RefreshData();
            SetResult(_lblResult, $"🔄 匹配成功 {ok} 条，未匹配 {miss} 条",
                ok > 0 ? UIHelper.C.Success : Color.OrangeRed);
        }

        private void ResendSelected()
        {
            if (_grid.SelectedRows.Count == 0) { Tip("请先选中一行或多行"); return; }
            if (!Confirm($"确认把选中的 {_grid.SelectedRows.Count} 条标记为待推送？")) return;

            int n = 0;
            using IDbConnection db = DbHelper.Open();
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (!long.TryParse(row.Cells["Id"].Value?.ToString(), out long id)) continue;
                try
                {
                    db.Execute(
                        "UPDATE run_record SET push_status='Pending', push_error=NULL WHERE id=@id",
                        new { id });
                    n++;
                }
                catch (Exception ex) { Log.Warn(ex, "Resend id={0} 失败", id); }
            }
            RefreshData();
            SetOk(_lblResult, $"⚡ 已重置 {n} 条为待推送");
        }

        private void AdminMarkSuccess()
        {
            if (_grid.SelectedRows.Count != 1) { Tip("请选中且仅选中一条记录"); return; }
            if (!long.TryParse(_grid.SelectedRows[0].Cells["Id"].Value?.ToString(), out long id)) return;

            string note = Prompt("请输入管理员备注（将记入 push_error 字段）：", "");
            if (note == null) return;

            try
            {
                _repo.MarkSuccessAdmin(id, note);
                RefreshData();
                SetOk(_lblResult, $"✅ 已标记 #{id} 为成功");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MarkSuccessAdmin id={0}", id);
                SetError(_lblResult, "❌ 标记失败：" + ex.Message);
            }
        }

        // ══════════════════════════════════════════════════════
        // 详情对话框
        // ══════════════════════════════════════════════════════

        private void OpenDetail(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _items.Count) return;
            var r = _items[rowIndex];

            using var f = new Form
            {
                Text = $"运行记录 #{r.Id}  设备 {r.DeviceCode}  车牌 {r.VehicleNo ?? "(未匹配)"}",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(960, 640),
                BackColor = UIHelper.C.Surface
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2,
                BackColor = UIHelper.C.Surface
            };
            f.Controls.Add(split);
            f.Shown += (s, e) =>
            {
                int min = 200, want = 380;
                int dist = Math.Max(min, split.Width - want);
                int max = split.Width - min;
                if (max >= min)
                {
                    split.Panel2MinSize    = min;
                    split.SplitterDistance = Math.Min(dist, max);
                }
            };

            // 左：信息网格（key/value）
            var info = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                RowHeadersVisible = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 9f),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeight = 26,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
            };
            info.Columns.Add("K", "字段");
            info.Columns.Add("V", "值");
            info.Columns[0].Width = 120;
            info.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            void Row(string k, string v) => info.Rows.Add(k, v ?? "-");
            Row("ID",            r.Id.ToString());
            Row("设备编码",      r.DeviceCode);
            Row("设备类型",      r.DeviceType);
            Row("开始时间",      r.StartTime);
            Row("结束时间",      r.EndTime);
            Row("时长(秒)",      r.RunTimeSec.ToString());
            Row("电流 末/最大/最小/中位", $"{Fmt(r.Currents)} / {Fmt(r.CurrentsMax)} / {Fmt(r.CurrentsMin)} / {Fmt(r.CurrentsMedian)}");
            Row("水压 末/最大/最小/中位", $"{Fmt(r.WaterPressure)} / {Fmt(r.WaterPressureMax)} / {Fmt(r.WaterPressureMin)} / {Fmt(r.WaterPressureMedian)}");
            Row("流量 末/最大/最小/中位", $"{Fmt(r.FlowQuantity)} / {Fmt(r.FlowQuantityMax)} / {Fmt(r.FlowQuantityMin)} / {Fmt(r.FlowQuantityMedian)}");
            Row("车牌号",        r.VehicleNo);
            Row("车辆图(本地)",  r.VehiclePicLocal);
            Row("车牌图(本地)",  r.VehicleNoPicLocal);
            Row("车辆图(URL)",   r.VehiclePic);
            Row("车牌图(URL)",   r.VehicleNoPic);
            Row("关闭原因",      r.CloseReason);
            Row("推送状态",      r.PushStatus);
            Row("推送错误/备注", r.PushError);
            Row("创建时间",      r.CreatedAt);
            split.Panel1.Controls.Add(info);

            // 右：图片预览
            var right = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1, RowCount = 2,
                Padding = new Padding(8),
                BackColor = UIHelper.C.Surface
            };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            var picVeh = new PictureBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black
            };
            var picPlate = new PictureBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black
            };
            right.Controls.Add(picVeh,   0, 0);
            right.Controls.Add(picPlate, 0, 1);
            split.Panel2.Controls.Add(right);

            LoadInto(picVeh,   r.VehiclePicLocal,   r.VehiclePic);
            LoadInto(picPlate, r.VehicleNoPicLocal, r.VehicleNoPic);

            f.FormClosed += (s, e) =>
            {
                picVeh.Image?.Dispose();
                picPlate.Image?.Dispose();
            };
            f.ShowDialog(this);
        }

        private static void LoadInto(PictureBox box, string localRel, string url)
        {
            box.Image?.Dispose();
            box.Image = null;
            if (!string.IsNullOrEmpty(localRel))
            {
                string full = Path.IsPathRooted(localRel)
                    ? localRel
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, localRel);
                if (File.Exists(full))
                {
                    try { box.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(full))); return; }
                    catch (Exception ex) { Log.Debug(ex, "本地图片加载失败 {0}", full); }
                }
            }
            if (!string.IsNullOrEmpty(url))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var bytes = await Http.GetByteArrayAsync(url);
                        var img = Image.FromStream(new MemoryStream(bytes));
                        box.BeginInvoke((Action)(() =>
                        {
                            box.Image?.Dispose();
                            box.Image = img;
                        }));
                    }
                    catch (Exception ex) { Log.Debug(ex, "远程图片加载失败 {0}", url); }
                });
            }
        }

        // 简易 Prompt 弹窗（PanelBase 没现成的）
        private static string Prompt(string label, string def)
        {
            using var f = new Form
            {
                Text = "输入",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(420, 160),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false
            };
            var lbl = new Label { Text = label, Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 8, 8, 0) };
            var tb  = new TextBox { Text = def ?? "", Dock = DockStyle.Top, Margin = new Padding(8) };
            var ok  = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 80 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 80 };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            f.Controls.Add(tb);
            f.Controls.Add(lbl);
            f.Controls.Add(bottom);
            return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
        }
    }
}
