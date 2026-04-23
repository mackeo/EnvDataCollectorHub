using System;
using System.Windows.Forms;
using EnvDataCollector.Data;
using EnvDataCollector.Forms;
using NLog;

namespace EnvDataCollector
{
    static class Program
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                Log.Error(e.Exception, "UI线程未处理异常");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "后台线程未处理异常");

            try
            {
                DatabaseInitializer.Initialize();
                Log.Info("=== 原料大棚洗车与除尘数据采集程序启动 ===");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "程序启动失败");
                MessageBox.Show("启动失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { LogManager.Shutdown(); }
        }
    }
}
