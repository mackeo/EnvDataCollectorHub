using System;
using System.IO;
using System.Runtime.InteropServices;
using NLog;

namespace EnvDataCollector.Services.Hk
{
    /// <summary>
    /// 海康 SDK 启动/清理包装。
    /// 负责设置 DLL 查找路径（按进程位数自适应），初始化 SDK，配置连接/重连参数与日志。
    /// Program.Main 启动时调 Startup()，退出前调 Shutdown()。
    /// </summary>
    public static class HikSdkBootstrap
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static bool _initialized;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        public static void Startup()
        {
            if (_initialized) return;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string sub     = Environment.Is64BitProcess ? "Win64" : "Win32";
            string sdkDir  = Path.Combine(baseDir, "Libs", "Hksdk", sub);

            if (!Directory.Exists(sdkDir))
            {
                Log.Warn("海康 SDK 目录不存在：{0}，Camera 功能将不可用", sdkDir);
                return;
            }

            if (!SetDllDirectory(sdkDir))
                Log.Warn("SetDllDirectory 失败（LastError={0}），仍尝试调用 NET_DVR_Init",
                    Marshal.GetLastWin32Error());

            if (!CHCNetSDK.NET_DVR_Init())
            {
                uint err = CHCNetSDK.NET_DVR_GetLastError();
                Log.Error("NET_DVR_Init 失败，错误码 {0}", err);
                return;
            }

            CHCNetSDK.NET_DVR_SetConnectTime(2000, 1);
            CHCNetSDK.NET_DVR_SetReconnect(10000, 1);

            string logDir = Path.Combine(baseDir, "logs", "hik");
            Directory.CreateDirectory(logDir);
            CHCNetSDK.NET_DVR_SetLogToFile(3, logDir, true);

            _initialized = true;
            Log.Info("海康 SDK 初始化完成（位数={0}，DLL 目录={1}）",
                Environment.Is64BitProcess ? "x64" : "x86", sdkDir);
        }

        public static void Shutdown()
        {
            if (!_initialized) return;
            try { CHCNetSDK.NET_DVR_Cleanup(); }
            catch (Exception ex) { Log.Warn(ex, "NET_DVR_Cleanup 异常"); }
            _initialized = false;
        }

        public static bool IsInitialized => _initialized;
    }
}
