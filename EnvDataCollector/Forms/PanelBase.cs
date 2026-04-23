using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace EnvDataCollector.Forms
{
    /// <summary>
    /// 所有功能 Panel 的基类。
    /// 统一提供：结果标签辅助、对话框快捷方式、启用/禁用切换按钮管理。
    /// </summary>
    [DesignerCategory("Code")]   // 强制以代码视图打开，避免 VS Designer 试图实例化抽象基类
    public abstract class PanelBase : UserControl, IRefreshable
    {
        protected PanelBase() { Dock = DockStyle.Fill; }

        public abstract void RefreshData();

        // ── 结果标签辅助 ──────────────────────────────────────
        protected static void SetResult(Label lbl, string msg, Color color)
        {
            lbl.Text      = msg;
            lbl.ForeColor = color;
        }
        protected static void SetOk   (Label lbl, string msg) => SetResult(lbl, msg, UIHelper.C.Success);
        protected static void SetError(Label lbl, string msg) => SetResult(lbl, msg, Color.OrangeRed);
        protected static void SetInfo (Label lbl, string msg) => SetResult(lbl, msg, Color.Gray);

        // ── 对话框快捷方式 ────────────────────────────────────
        protected static void Tip(string msg) =>
            MessageBox.Show(msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);

        protected static bool Confirm(string msg, string title = "确认") =>
            MessageBox.Show(msg, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == DialogResult.Yes;

        // ── 启用/禁用切换按钮辅助 ────────────────────────────
        /// <summary>刷新切换按钮的文字和颜色</summary>
        protected static void SyncToggleBtn(Button btn, bool isEnabled)
        {
            btn.Text      = isEnabled ? "⏸ 禁用" : "▶ 启用";
            btn.BackColor = isEnabled ? UIHelper.C.Warning : UIHelper.C.Success;
        }
    }
}
