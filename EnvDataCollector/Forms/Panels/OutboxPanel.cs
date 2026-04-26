using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using NLog;

namespace EnvDataCollector.Forms.Panels
{
    public class OutboxPanel : PanelBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly MainForm _main;
        private readonly OutboxRepository _repo = new();

        private DataGridView   _grid;
        private ComboBox       _cmbStatus;
        private DateTimePicker _dtFrom, _dtTo;
        private Label          _lblCount;
        private List<OutboxMessageEntity> _items = new();

        public OutboxPanel(MainForm main) { _main = main; BuildUI(); }

        private void BuildUI()
        {
            _lblCount  = UIHelper.ResultLabel();
            _dtFrom    = UIHelper.MakeDatePicker(DateTime.Today);
            _dtTo      = UIHelper.MakeDatePicker(DateTime.Today.AddDays(1).AddSeconds(-1));
            _cmbStatus = UIHelper.MakeToolbarCombo(85, "全部", "Pending", "Failed", "Success");

            var btnLoad   = UIHelper.MakeBtn("🔍 查询");
            var btnRetry  = UIHelper.MakeBtn("⚡ 批量重试", UIHelper.C.Success);
            var btnRunNow = UIHelper.MakeBtn("▶ 立即推送", UIHelper.C.Dark);
            var btnExport = UIHelper.MakeBtn("📥 导出失败", UIHelper.C.Purple);
            btnLoad.Click   += (s, e) => RefreshData();
            btnRetry.Click  += (s, e) => RetrySelected();
            btnRunNow.Click += (s, e) => RunPusher();
            btnExport.Click += (s, e) => ExportFailed();

            var toolbar = UIHelper.MakeToolbar(
                UIHelper.InlineLabel("从:"), _dtFrom,
                UIHelper.InlineLabel("至:"), _dtTo,
                UIHelper.InlineLabel("状态:"), _cmbStatus,
                btnLoad, btnRetry, btnRunNow, btnExport, _lblCount);

            _grid = UIHelper.MakeGrid();
            _grid.Columns.AddRange(
                UIHelper.Col("Id",      "ID",       65),
                UIHelper.Col("Type",    "类型",     90),
                UIHelper.Col("Status",  "状态",     70),
                UIHelper.Col("Retry",   "重试",     45),
                UIHelper.Col("Code",    "HTTP",     50),
                UIHelper.Col("Url",     "目标 URL",260),
                UIHelper.Col("Created", "创建时间",145),
                UIHelper.Col("Next",    "下次重试",145),
                UIHelper.Col("Error",   "错误信息",  0, true));
            _grid.CellDoubleClick += (s, e) => OpenDetail(e.RowIndex);

            Controls.Add(_grid);
            Controls.Add(UIHelper.MakeSeparator());
            Controls.Add(toolbar);
        }

        // ══════════════════════════════════════════════════════
        // 加载
        // ══════════════════════════════════════════════════════

        public override void RefreshData()
        {
            DateTime? from = _dtFrom.Value;
            DateTime? to   = _dtTo.Value;
            string status  = _cmbStatus.SelectedItem?.ToString();
            if (status == "全部") status = null;

            try
            {
                _items = _repo.GetPage(status, from, to, 500).ToList();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "查询 push_outbox 失败");
                SetError(_lblCount, "❌ 查询失败：" + ex.Message);
                return;
            }

            _grid.Rows.Clear();
            foreach (var m in _items)
            {
                int ri = _grid.Rows.Add(
                    m.Id, m.MessageType, m.Status,
                    $"{m.RetryCount}/{m.MaxRetry}",
                    m.LastHttpCode?.ToString() ?? "-",
                    m.TargetUrl,
                    m.CreatedAt, m.NextRetryTime,
                    m.LastError);
                if (m.Status == "Failed")
                    _grid.Rows[ri].DefaultCellStyle.ForeColor = Color.OrangeRed;
                else if (m.Status == "Success")
                    _grid.Rows[ri].DefaultCellStyle.ForeColor = Color.Gray;
            }
            SetOk(_lblCount, $"✅ 共 {_items.Count} 条" + (_items.Count >= 500 ? "（已截断到 500）" : ""));
        }

        // ══════════════════════════════════════════════════════
        // 操作
        // ══════════════════════════════════════════════════════

        private void RetrySelected()
        {
            if (_grid.SelectedRows.Count == 0) { Tip("请先选中要重试的行"); return; }
            if (!Confirm($"确认把选中的 {_grid.SelectedRows.Count} 条重置为待推送？")) return;

            int n = 0;
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (!long.TryParse(row.Cells["Id"].Value?.ToString(), out long id)) continue;
                try { _repo.ResetToPending(id); n++; }
                catch (Exception ex) { Log.Warn(ex, "ResetToPending id={0}", id); }
            }
            RefreshData();
            SetOk(_lblCount, $"⚡ 已重置 {n} 条");
        }

        private void RunPusher()
        {
            if (_main?.Pusher == null) { Tip("PushWorker 未初始化"); return; }
            try
            {
                int ok = _main.Pusher.RunOnce();
                RefreshData();
                SetOk(_lblCount, $"▶ 已触发推送，本轮成功 {ok} 条");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RunPusher 失败");
                SetError(_lblCount, "❌ 推送触发失败：" + ex.Message);
            }
        }

        private void ExportFailed()
        {
            try
            {
                var failed = _repo.GetPage("Failed", null, null, 5000).ToList();
                if (failed.Count == 0) { Tip("没有失败记录可导出"); return; }

                using var dlg = new SaveFileDialog
                {
                    Filter = "CSV 文件|*.csv",
                    FileName = $"outbox_failed_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };
                if (dlg.ShowDialog() != DialogResult.OK) return;

                var sb = new StringBuilder();
                sb.AppendLine("Id,MessageType,Status,RetryCount,LastHttpCode,TargetUrl,CreatedAt,NextRetryTime,LastError");
                foreach (var m in failed)
                {
                    sb.AppendLine(string.Join(",",
                        m.Id, Csv(m.MessageType), Csv(m.Status), m.RetryCount,
                        m.LastHttpCode?.ToString() ?? "",
                        Csv(m.TargetUrl), Csv(m.CreatedAt), Csv(m.NextRetryTime), Csv(m.LastError)));
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                SetOk(_lblCount, $"📥 已导出 {failed.Count} 条失败记录");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ExportFailed 异常");
                SetError(_lblCount, "❌ 导出失败：" + ex.Message);
            }
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool needsQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
            if (!needsQuote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private void OpenDetail(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _items.Count) return;
            var m = _items[rowIndex];

            using var f = new Form
            {
                Text = $"push_outbox #{m.Id}  类型 {m.MessageType}  状态 {m.Status}",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(820, 560),
                BackColor = UIHelper.C.Surface
            };
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                WordWrap = true
            };
            var sb = new StringBuilder();
            sb.AppendLine($"ID:           {m.Id}");
            sb.AppendLine($"MessageType:  {m.MessageType}");
            sb.AppendLine($"Status:       {m.Status}");
            sb.AppendLine($"RetryCount:   {m.RetryCount} / {m.MaxRetry}");
            sb.AppendLine($"LastHttpCode: {m.LastHttpCode?.ToString() ?? "-"}");
            sb.AppendLine($"TargetUrl:    {m.TargetUrl}");
            sb.AppendLine($"CreatedAt:    {m.CreatedAt}");
            sb.AppendLine($"UpdatedAt:    {m.UpdatedAt}");
            sb.AppendLine($"NextRetryTime:{m.NextRetryTime}");
            sb.AppendLine($"RelatedTable: {m.RelatedTable}  RelatedId: {m.RelatedId}");
            sb.AppendLine($"LastError:    {m.LastError}");
            sb.AppendLine();
            sb.AppendLine("── PayloadJson ──");
            sb.AppendLine(m.PayloadJson);
            box.Text = sb.ToString();
            f.Controls.Add(box);
            f.ShowDialog(this);
        }
    }
}
