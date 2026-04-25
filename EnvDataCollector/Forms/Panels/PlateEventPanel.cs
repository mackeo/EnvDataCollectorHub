using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using NLog;

namespace EnvDataCollector.Forms.Panels
{
    /// <summary>
    /// 车牌识别事件浏览：按时间范围 + 设备 + 车牌过滤，左侧列表 + 右侧图片预览，
    /// 双击行弹出大图，可单条删除（连带磁盘 jpg）。
    /// </summary>
    public class PlateEventPanel : PanelBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        private readonly PlateEventRepository _repo    = new();
        private readonly DeviceRepository     _devRepo = new();

        private DateTimePicker _dtFrom, _dtTo;
        private ComboBox       _cmbDevice;
        private TextBox        _txtPlate;
        private DataGridView   _grid;
        private PictureBox     _picVehicle, _picPlate;
        private Label          _lblInfo, _lblResult, _lblCount;

        // 当前列表对应的实体（按 grid 行序），点击行时拿来加载预览
        private List<PlateEventEntity> _items = new();

        // SplitContainer 一次性应用 SplitterDistance 的 flag；之后任由用户拖动
        private bool _splitInited;

        public PlateEventPanel(MainForm main) { BuildUI(); }

        // ══════════════════════════════════════════════════════
        // UI
        // ══════════════════════════════════════════════════════

        private void BuildUI()
        {
            // 工具栏
            _dtFrom = UIHelper.MakeDatePicker(DateTime.Today);
            _dtTo   = UIHelper.MakeDatePicker(DateTime.Now);
            _cmbDevice = UIHelper.MakeToolbarCombo(160);
            _txtPlate  = UIHelper.MakeToolbarTextBox(110, "车牌包含…");
            var btnQuery = UIHelper.MakeBtn("🔍 查询",   UIHelper.C.Primary);
            var btnToday = UIHelper.MakeBtn("📅 今日",   UIHelper.C.Dark);
            var btnRecent= UIHelper.MakeBtn("⏱ 近1小时",UIHelper.C.Dark);
            var btnDel   = UIHelper.MakeBtn("🗑 删除",   UIHelper.C.Danger);
            _lblResult   = UIHelper.ResultLabel();

            btnQuery.Click  += (s, e) => RefreshData();
            btnToday.Click  += (s, e) => { _dtFrom.Value = DateTime.Today;            _dtTo.Value = DateTime.Now; RefreshData(); };
            btnRecent.Click += (s, e) => { _dtFrom.Value = DateTime.Now.AddHours(-1); _dtTo.Value = DateTime.Now; RefreshData(); };
            btnDel.Click    += (s, e) => DeleteSelected();

            var toolbar = UIHelper.MakeToolbar(
                UIHelper.InlineLabel("从"), _dtFrom,
                UIHelper.InlineLabel("到"), _dtTo,
                UIHelper.InlineLabel("设备"), _cmbDevice,
                _txtPlate, btnQuery, btnToday, btnRecent, btnDel, _lblResult);

            // 主体：左 grid + 右图片预览
            // SplitterDistance / Panel2MinSize 不能在 new 时设：此刻 Width 是默认值（约 150），
            // 任何 >= Width 的 SplitterDistance 都会抛 ArgumentException → 启动失败。
            // 改成挂 HandleCreated + Resize 一次性应用（用 _splitInited flag 防覆盖用户拖动）。
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2,
                BackColor = UIHelper.C.Surface
            };
            split.HandleCreated += (s, e) => ApplySplitLayout(split);
            split.Resize        += (s, e) => ApplySplitLayout(split);

            // 左：列表
            _grid = UIHelper.MakeGrid();
            _grid.Columns.AddRange(
                UIHelper.Col("Id",   "ID",       60),
                UIHelper.Col("Time", "事件时间", 145),
                UIHelper.Col("Dev",  "设备",     130),
                UIHelper.Col("Plate","车牌",     110),
                UIHelper.Col("Conf", "置信度",   65),
                UIHelper.Col("Pic",  "本地图片", 0, true));
            _grid.RowEnter  += (s, e) => LoadPreview(e.RowIndex);
            _grid.CellClick += (s, e) => LoadPreview(e.RowIndex);
            _grid.CellDoubleClick += (s, e) => OpenBigPicture(e.RowIndex);
            split.Panel1.Controls.Add(_grid);

            // 右：图片预览 + 信息
            var right = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(8),
                BackColor = UIHelper.C.Surface
            };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));

            _lblInfo = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                ForeColor = UIHelper.C.Dark,
                Text = "（请选择左侧一行）"
            };
            _picVehicle = new PictureBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black
            };
            _picPlate = new PictureBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black
            };
            _picVehicle.DoubleClick += (s, e) => OpenBigPicture(_grid.CurrentRow?.Index ?? -1, useVehicle: true);
            _picPlate.DoubleClick   += (s, e) => OpenBigPicture(_grid.CurrentRow?.Index ?? -1, useVehicle: false);

            _lblCount = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                ForeColor = UIHelper.C.TextMuted,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                Text = "提示：双击列表或图片可查看大图。"
            };

            right.Controls.Add(_lblInfo,    0, 0);
            right.Controls.Add(_picVehicle, 0, 1);
            right.Controls.Add(_picPlate,   0, 2);
            right.Controls.Add(_lblCount,   0, 3);
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            Controls.Add(UIHelper.MakeSeparator());
            Controls.Add(toolbar);
        }

        // ══════════════════════════════════════════════════════
        // 数据加载
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// 第一次有合理 Width 时设置 SplitterDistance / Panel2MinSize。
        /// HandleCreated 是 native handle 出现的时机；Resize 是兜底（保证 LayoutEngine
        /// 至少给过一次合理 Width）。设成功后 _splitInited 置 true，让用户拖动生效。
        /// </summary>
        private void ApplySplitLayout(SplitContainer split)
        {
            if (_splitInited)        return;
            if (split.Width < 600)   return;   // 主窗未铺开前不动

            int panel2Min = 200;
            int rightWanted = 360;
            int dist = Math.Max(panel2Min, split.Width - rightWanted);

            // 数学保证：dist ∈ [panel2Min, Width - panel2Min]，但保险起见再夹一次
            int max = split.Width - panel2Min;
            if (max < panel2Min) return;       // Width 太小，留给下次 Resize
            if (dist > max) dist = max;

            try
            {
                split.Panel2MinSize    = panel2Min;
                split.SplitterDistance = dist;
                _splitInited = true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ApplySplitLayout 失败（Width={0}，dist={1}）", split.Width, dist);
            }
        }

        public override void RefreshData()
        {
            // 设备下拉：保留选中
            int prev = _cmbDevice.SelectedItem is UIHelper.Item p ? p.Id : 0;
            _cmbDevice.Items.Clear();
            _cmbDevice.Items.Add(new UIHelper.Item(0, "全部设备"));
            foreach (var d in _devRepo.GetAll().Where(x => x.DeviceType == "洗车机"))
                _cmbDevice.Items.Add(new UIHelper.Item(d.Id, $"{d.DeviceName} [{d.DeviceCode}]"));
            int restoreIdx = 0;
            for (int i = 0; i < _cmbDevice.Items.Count; i++)
                if (_cmbDevice.Items[i] is UIHelper.Item it && it.Id == prev) { restoreIdx = i; break; }
            _cmbDevice.SelectedIndex = restoreIdx;

            DateTime from = _dtFrom.Value;
            DateTime to   = _dtTo.Value;
            if (to < from) (from, to) = (to, from);
            int? deviceId = _cmbDevice.SelectedItem is UIHelper.Item it2 && it2.Id > 0 ? it2.Id : (int?)null;
            string plate = _txtPlate.Text.Trim();

            try
            {
                _items = _repo.Query(from, to, deviceId, plate, limit: 500).ToList();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "查询车牌事件失败");
                SetError(_lblResult, "❌ 查询失败：" + ex.Message);
                return;
            }

            _grid.Rows.Clear();
            var devNames = _devRepo.GetAll().ToDictionary(d => d.Id, d => $"{d.DeviceName} [{d.DeviceCode}]");
            foreach (var ev in _items)
            {
                _grid.Rows.Add(
                    ev.Id,
                    ev.EventTime,
                    devNames.TryGetValue(ev.DeviceId, out var n) ? n : $"⚠ #{ev.DeviceId}",
                    ev.PlateNo,
                    ev.Confidence.HasValue ? $"{ev.Confidence.Value:P0}" : "-",
                    ev.VehiclePicLocal ?? ev.PlatePicLocal ?? "-");
            }
            SetOk(_lblResult, $"✅ 共 {_items.Count} 条" + (_items.Count >= 500 ? "（已截断到 500 条）" : ""));
            ClearPreview();
        }

        // ══════════════════════════════════════════════════════
        // 预览
        // ══════════════════════════════════════════════════════

        private void LoadPreview(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _items.Count) return;
            var ev = _items[rowIndex];

            _lblInfo.Text =
                $"#{ev.Id}  车牌 {ev.PlateNo}  时间 {ev.EventTime}  " +
                (ev.Confidence.HasValue ? $"置信度 {ev.Confidence.Value:P0}" : "");

            LoadInto(_picVehicle, ev.VehiclePicLocal, ev.VehiclePicUrl);
            LoadInto(_picPlate,   ev.PlatePicLocal,   ev.PlatePicUrl);
        }

        private void ClearPreview()
        {
            _picVehicle.Image?.Dispose(); _picVehicle.Image = null;
            _picPlate.Image?.Dispose();   _picPlate.Image   = null;
            _lblInfo.Text = "（请选择左侧一行）";
        }

        private static void LoadInto(PictureBox box, string localRel, string url)
        {
            box.Image?.Dispose();
            box.Image = null;

            // 1) 优先本地
            if (!string.IsNullOrEmpty(localRel))
            {
                string full = Path.IsPathRooted(localRel)
                    ? localRel
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, localRel);
                if (File.Exists(full))
                {
                    try
                    {
                        // 通过 byte[] 读取避免锁文件
                        box.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(full)));
                        return;
                    }
                    catch (Exception ex) { Log.Debug(ex, "本地图片加载失败：{0}", full); }
                }
            }

            // 2) 远程 URL fallback
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
                    catch (Exception ex) { Log.Debug(ex, "远程图片加载失败：{0}", url); }
                });
            }
        }

        private void OpenBigPicture(int rowIndex, bool useVehicle = true)
        {
            if (rowIndex < 0 || rowIndex >= _items.Count) return;
            var ev   = _items[rowIndex];
            var rel  = useVehicle ? ev.VehiclePicLocal : ev.PlatePicLocal;
            var url  = useVehicle ? ev.VehiclePicUrl   : ev.PlatePicUrl;
            if (string.IsNullOrEmpty(rel) && string.IsNullOrEmpty(url))
                rel = ev.VehiclePicLocal ?? ev.PlatePicLocal;

            using var f = new Form
            {
                Text = $"#{ev.Id}  {ev.PlateNo}  {ev.EventTime}",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(1024, 768),
                BackColor = Color.Black
            };
            var pb = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            f.Controls.Add(pb);
            LoadInto(pb, rel, url);
            f.ShowDialog(this);
            pb.Image?.Dispose();
        }

        // ══════════════════════════════════════════════════════
        // 删除
        // ══════════════════════════════════════════════════════

        private void DeleteSelected()
        {
            if (_grid.SelectedRows.Count == 0) { Tip("请先选中要删除的行"); return; }
            int n = _grid.SelectedRows.Count;
            if (!Confirm($"确认删除选中的 {n} 条车牌事件？\n（关联磁盘图片也会一起删除）")) return;

            int ok = 0, fail = 0;
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (!long.TryParse(row.Cells["Id"].Value?.ToString(), out long id)) continue;
                var ev = _items.FirstOrDefault(x => x.Id == id);
                try
                {
                    _repo.Delete(id);
                    TryDeleteFile(ev?.VehiclePicLocal);
                    TryDeleteFile(ev?.PlatePicLocal);
                    ok++;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "删除车牌事件失败 id={0}", id);
                    fail++;
                }
            }
            RefreshData();
            if (fail == 0) SetOk(_lblResult,    $"✅ 已删除 {ok} 条");
            else           SetError(_lblResult, $"⚠ 成功 {ok}，失败 {fail}");
        }

        private static void TryDeleteFile(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return;
            try
            {
                string full = Path.IsPathRooted(rel)
                    ? rel
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rel);
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex) { Log.Debug(ex, "删图片失败：{0}", rel); }
        }
    }
}
