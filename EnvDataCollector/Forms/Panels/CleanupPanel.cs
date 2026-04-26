using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDataCollector.Data.Repositories;
using NLog;

namespace EnvDataCollector.Forms.Panels
{
    public class CleanupPanel : PanelBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly MainForm _main;
        private readonly AppSettingRepository _settings = new();

        private NumericUpDown _numDays, _numHours;
        private Label         _lblResult;

        public CleanupPanel(MainForm main) { _main = main; BuildUI(); }

        private void BuildUI()
        {
            _lblResult = UIHelper.ResultLabel();
            _numDays   = UIHelper.MakeNumeric(1, 365, 30);
            _numHours  = UIHelper.MakeNumeric(1, 168, 24);

            var note = new Label
            {
                Text =
                    "⚠ 仅清理 PushStatus=Success 的历史数据；plate_event 按创建时间整体清理。\n" +
                    "   关联图片目录（images/{deviceCode}/yyyyMMdd/）也会按截止日期整目录删除。\n" +
                    "   失败/待推送数据保留以便补传。",
                ForeColor = Color.OrangeRed,
                AutoSize  = true,
                Padding   = new Padding(0, 4, 0, 0)
            };

            var tbl = UIHelper.MakeFormTable(labelWidth: 110);
            tbl.AddRow("保留天数",    _numDays);
            tbl.AddRow("清理周期(h)", _numHours);
            tbl.AddRowSpan(note, height: 60);

            var btnSave  = UIHelper.MakeBtn("💾 保存策略");
            var btnClean = UIHelper.MakeBtn("🗑 立即清理", UIHelper.C.Danger);
            btnSave.Click  += (s, e) => SaveSettings();
            btnClean.Click += (s, e) => CleanNow(btnClean);
            tbl.AddBtnRow(btnSave, btnClean, _lblResult);

            Controls.Add(UIHelper.WrapFormPanel(tbl, height: 240,
                padding: new Padding(20, 16, 20, 8)));
        }

        public override void RefreshData()
        {
            _numDays.Value  = Clamp(_settings.Get<int>(SK.CleanRetentionDays, 30),  1, 365);
            _numHours.Value = Clamp(_settings.Get<int>(SK.CleanIntervalHours, 24),  1, 168);
        }

        private static decimal Clamp(int v, int lo, int hi) =>
            v < lo ? lo : (v > hi ? hi : v);

        private void SaveSettings()
        {
            try
            {
                _settings.Set(SK.CleanRetentionDays, (int)_numDays.Value);
                _settings.Set(SK.CleanIntervalHours, (int)_numHours.Value);
                SetOk(_lblResult, $"✅ 已保存（清理周期改动需重启程序生效）");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Cleanup 保存失败");
                SetError(_lblResult, "❌ 保存失败：" + ex.Message);
            }
        }

        private void CleanNow(Button btn)
        {
            if (_main?.Cleanup == null) { Tip("CleanupWorker 未初始化"); return; }
            int days = (int)_numDays.Value;
            if (!Confirm($"确认立即清理保留天数 < {days} 天的 Success 历史数据 + 早于 {DateTime.Now.AddDays(-days):yyyy-MM-dd} 的图片目录？"))
                return;

            // 临时把 UI 上的保留天数生效（让用户改了滑块直接立即按用即可，无需先点"保存"）
            try { _settings.Set(SK.CleanRetentionDays, days); } catch { }

            btn.Enabled = false;
            SetInfo(_lblResult, "正在清理…");
            Task.Run(() =>
            {
                Services.CleanupWorker.CleanupResult r;
                try { r = _main.Cleanup.RunOnce(); }
                catch (Exception ex) { r = null; Log.Error(ex, "CleanNow 异常"); }

                BeginInvoke((Action)(() =>
                {
                    btn.Enabled = true;
                    if (r == null)
                    {
                        SetError(_lblResult, "❌ 清理异常，详见日志");
                    }
                    else if (r.Errors.Count > 0)
                    {
                        SetError(_lblResult, "⚠ " + r.Summary());
                    }
                    else
                    {
                        SetOk(_lblResult, "✅ " + r.Summary());
                    }
                }));
            });
        }
    }
}
