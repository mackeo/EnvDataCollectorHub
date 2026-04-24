using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using EnvDataCollector.Services;
using Newtonsoft.Json;
using NLog;

namespace EnvDataCollector.Services.Hk
{
    /// <summary>
    /// 海康摄像头采集服务：登录 -> 布防 -> 车牌事件回调 -> 图片落盘 -> plate_event 入库。
    /// 多相机共用一个全局 MSGCallBack_V31，通过 pAlarmer.lUserID 反查 deviceId。
    /// Start / Stop / Reload 必须在 UI 线程或同一个调度上下文里调用（内部不做并发保护）。
    /// </summary>
    public sealed class CameraService
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly CameraConfigRepository _camRepo   = new();
        private readonly DeviceRepository       _devRepo   = new();
        private readonly PlateEventRepository   _plateRepo = new();

        // userId -> session 信息（含 deviceId、alarmHandle、配置）
        private readonly Dictionary<int, CameraSession> _sessions = new();

        // 回调委托字段：必须作为字段长期持有，防止被 GC，否则 SDK 回调时 AccessViolation
        private CHCNetSDK.MSGCallBack_V31 _callback;

        // 事件处理用后台 worker：回调里只 marshal + 拷贝字节，把耗时工作丢到队列
        private BlockingCollection<PlateWorkItem> _queue;
        private Task _worker;

        public bool Running { get; private set; }
        public int  ActiveCount => _sessions.Count;

        public void Start()
        {
            if (Running) return;
            if (!HikSdkBootstrap.IsInitialized)
            {
                Log.Warn("CameraService.Start 被跳过：海康 SDK 未初始化");
                return;
            }

            _callback = OnMessage;
            if (!CHCNetSDK.NET_DVR_SetDVRMessageCallBack_V31(_callback, IntPtr.Zero))
                Log.Warn("NET_DVR_SetDVRMessageCallBack_V31 失败，错误码 {0}",
                    CHCNetSDK.NET_DVR_GetLastError());

            _queue  = new BlockingCollection<PlateWorkItem>();
            _worker = Task.Run((Action)WorkerLoop);

            foreach (var cfg in _camRepo.GetAll(enabledOnly: true))
                TryOpen(cfg);

            Running = true;
            Log.Info("CameraService 已启动，当前会话数 {0}", _sessions.Count);
        }

        public void Stop()
        {
            if (!Running) return;

            foreach (var kv in _sessions)
            {
                try { if (kv.Value.AlarmHandle >= 0) CHCNetSDK.NET_DVR_CloseAlarmChan_V30(kv.Value.AlarmHandle); }
                catch (Exception ex) { Log.Debug(ex, "CloseAlarmChan 异常（userId={0}）", kv.Key); }
                try { CHCNetSDK.NET_DVR_Logout(kv.Key); }
                catch (Exception ex) { Log.Debug(ex, "Logout 异常（userId={0}）", kv.Key); }
            }
            _sessions.Clear();

            try { _queue?.CompleteAdding(); } catch { }
            try { _worker?.Wait(500); }       catch { }
            _queue  = null;
            _worker = null;

            _callback = null;   // 解除回调引用，配合下一轮 Start 重新赋值
            Running = false;
            Log.Info("CameraService 已停止");
        }

        public void Reload()
        {
            Stop();
            Start();
        }

        // ═══════════════════════════════════════════════════════════
        // 登录 + 布防
        // ═══════════════════════════════════════════════════════════

        private void TryOpen(CameraConfigEntity cfg)
        {
            var dev = _devRepo.GetById(cfg.DeviceId);
            if (dev == null)
            {
                Log.Warn("摄像头 #{0} 关联的设备 #{1} 不存在，跳过", cfg.Id, cfg.DeviceId);
                return;
            }

            string pwd = CryptoHelper.Decrypt(cfg.PasswordEnc);

            var login = new CHCNetSDK.NET_DVR_USER_LOGIN_INFO
            {
                sDeviceAddress = new byte[CHCNetSDK.NET_DVR_DEV_ADDRESS_MAX_LEN],
                sUserName      = new byte[CHCNetSDK.NET_DVR_LOGIN_USERNAME_MAX_LEN],
                sPassword      = new byte[CHCNetSDK.NET_DVR_LOGIN_PASSWD_MAX_LEN],
                wPort          = (ushort)cfg.Port,
                bUseAsynLogin  = false,
                byRes3         = new byte[119]
            };
            WriteAnsi(login.sDeviceAddress, cfg.Ip);
            WriteAnsi(login.sUserName,      cfg.Username);
            WriteAnsi(login.sPassword,      pwd);

            var devInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V40 { byRes2 = new byte[243] };
            int userId = CHCNetSDK.NET_DVR_Login_V40(ref login, ref devInfo);
            if (userId < 0)
            {
                Log.Warn("摄像头 {0}@{1} 登录失败，错误码 {2}",
                    cfg.Username, cfg.Ip, CHCNetSDK.NET_DVR_GetLastError());
                return;
            }

            var alarmParam = new CHCNetSDK.NET_DVR_SETUPALARM_PARAM
            {
                dwSize           = (uint)Marshal.SizeOf(typeof(CHCNetSDK.NET_DVR_SETUPALARM_PARAM)),
                byLevel          = 1,
                byAlarmInfoType  = 1,   // 使用新结构 NET_ITS_PLATE_RESULT
                byDeployType     = 1,
                byRes1           = new byte[2]
            };
            int alarmHandle = CHCNetSDK.NET_DVR_SetupAlarmChan_V41(userId, ref alarmParam);
            if (alarmHandle < 0)
            {
                Log.Warn("摄像头 {0}@{1} 布防失败，错误码 {2}",
                    cfg.Username, cfg.Ip, CHCNetSDK.NET_DVR_GetLastError());
                CHCNetSDK.NET_DVR_Logout(userId);
                return;
            }

            _sessions[userId] = new CameraSession
            {
                UserId      = userId,
                AlarmHandle = alarmHandle,
                DeviceId    = dev.Id,
                DeviceCode  = dev.DeviceCode,
                Config      = cfg
            };
            Log.Info("摄像头 {0}@{1} 登录+布防成功（userId={2}，alarm={3}）",
                cfg.Username, cfg.Ip, userId, alarmHandle);
        }

        // ═══════════════════════════════════════════════════════════
        // 消息回调（SDK 线程调用）
        // ═══════════════════════════════════════════════════════════

        private bool OnMessage(int lCommand, ref CHCNetSDK.NET_DVR_ALARMER pAlarmer,
            IntPtr pAlarmInfo, uint dwBufLen, IntPtr pUser)
        {
            try
            {
                if (lCommand != CHCNetSDK.COMM_ITS_PLATE_RESULT) return true;
                if (pAlarmInfo == IntPtr.Zero) return true;

                if (!_sessions.TryGetValue(pAlarmer.lUserID, out var sess))
                {
                    Log.Debug("收到未知来源的车牌事件 userId={0}", pAlarmer.lUserID);
                    return true;
                }

                var plate = Marshal.PtrToStructure<CHCNetSDK.NET_ITS_PLATE_RESULT>(pAlarmInfo);

                // 先把所有图片字节从 unmanaged 拷到托管内存（必须在回调里做，回调返回后 pBuffer 失效）
                var pics = new List<(byte type, byte[] data)>();
                if (plate.struPicInfo != null)
                {
                    int n = (int)Math.Min(plate.dwPicNum, (uint)plate.struPicInfo.Length);
                    for (int i = 0; i < n; i++)
                    {
                        var pic = plate.struPicInfo[i];
                        if (pic.pBuffer != IntPtr.Zero && pic.dwDataLen > 0)
                        {
                            var buf = new byte[pic.dwDataLen];
                            Marshal.Copy(pic.pBuffer, buf, 0, (int)pic.dwDataLen);
                            pics.Add((pic.byType, buf));
                        }
                    }
                }

                var q = _queue;
                if (q != null && !q.IsAddingCompleted)
                {
                    try
                    {
                        q.Add(new PlateWorkItem
                        {
                            DeviceId   = sess.DeviceId,
                            DeviceCode = sess.DeviceCode,
                            Config     = sess.Config,
                            Plate      = plate,
                            Pictures   = pics,
                            ReceivedAt = DateTime.Now
                        });
                    }
                    catch (InvalidOperationException) { /* Stop 与回调竞争，丢弃本次事件 */ }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "OnMessage 处理异常");
            }
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        // 后台 worker：写磁盘 + 写数据库
        // ═══════════════════════════════════════════════════════════

        private void WorkerLoop()
        {
            var q = _queue;
            if (q == null) return;
            // CompleteAdding 后 GetConsumingEnumerable 自然结束，无需 CancellationToken
            foreach (var item in q.GetConsumingEnumerable())
            {
                try { ProcessItem(item); }
                catch (Exception ex) { Log.Error(ex, "ProcessItem 异常"); }
            }
        }

        private void ProcessItem(PlateWorkItem it)
        {
            string plateNo   = DecodePlate(it.Plate.struPlateInfo.sLicense);
            double? confidence = it.Plate.struPlateInfo.byEntireBelieve > 0
                ? it.Plate.struPlateInfo.byEntireBelieve / 100.0
                : (double?)null;

            DateTime eventTime = it.ReceivedAt;  // 简化：以到达时间为准；SDK 里 struSnapFirstPicTime 可选扩展
            string timeStr = eventTime.ToString("yyyy-MM-dd HH:mm:ss");
            string dayDir  = eventTime.ToString("yyyyMMdd");
            long   stamp   = eventTime.Ticks;

            // 图片目录：exe\{image_store_path}\{deviceCode}\{yyyyMMdd}\
            string storeRoot = Path.IsPathRooted(it.Config.ImageStorePath)
                ? it.Config.ImageStorePath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, it.Config.ImageStorePath);
            string saveDir = Path.Combine(storeRoot, it.DeviceCode, dayDir);
            Directory.CreateDirectory(saveDir);

            string vehicleLocal = null, plateLocal = null;
            string vehicleUrl   = null, plateUrl   = null;

            for (int i = 0; i < it.Pictures.Count; i++)
            {
                var (type, data) = it.Pictures[i];
                // byType: 1=车辆图, 2=车牌图, 3=二值化图, 0/其它=未知；具体取决于设备型号
                string label = type switch
                {
                    1 => "vehicle",
                    2 => "plate",
                    _ => $"type{type}"
                };
                string fileName = $"{stamp}_{i}_{label}.jpg";
                string fullPath = Path.Combine(saveDir, fileName);
                try { File.WriteAllBytes(fullPath, data); }
                catch (Exception ex) { Log.Warn(ex, "写图片失败：{0}", fullPath); continue; }

                string relForUrl = $"{it.DeviceCode}/{dayDir}/{fileName}";
                string url       = $"{it.Config.ImageBaseUrl.TrimEnd('/')}/{relForUrl}";
                string relLocal  = Path.Combine(it.Config.ImageStorePath.TrimEnd('\\', '/'),
                                                it.DeviceCode, dayDir, fileName);

                if (type == 1 && vehicleLocal == null) { vehicleLocal = relLocal; vehicleUrl = url; }
                else if (type == 2 && plateLocal == null) { plateLocal = relLocal; plateUrl = url; }
                else if (vehicleLocal == null)             { vehicleLocal = relLocal; vehicleUrl = url; }
            }

            try
            {
                _plateRepo.Insert(new PlateEventEntity
                {
                    DeviceId        = it.DeviceId,
                    PlateNo         = plateNo,
                    EventTime       = timeStr,
                    Confidence      = confidence,
                    VehiclePicLocal = vehicleLocal,
                    PlatePicLocal   = plateLocal,
                    VehiclePicUrl   = vehicleUrl,
                    PlatePicUrl     = plateUrl,
                    RawJson         = SafeSerialize(it.Plate)
                });
                Log.Info("车牌事件入库：{0} @ {1}（设备 {2}，图片 {3} 张）",
                    plateNo, timeStr, it.DeviceCode, it.Pictures.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "plate_event Insert 失败：plate={0}", plateNo);
            }
        }

        private static string DecodePlate(byte[] src)
        {
            if (src == null || src.Length == 0) return "";
            int len = Array.IndexOf(src, (byte)0);
            if (len < 0) len = src.Length;
            try
            {
                return Encoding.GetEncoding("GBK").GetString(src, 0, len).Trim();
            }
            catch
            {
                return Encoding.ASCII.GetString(src, 0, len).Trim();
            }
        }

        private static string SafeSerialize(object obj)
        {
            try { return JsonConvert.SerializeObject(obj); }
            catch { return null; }
        }

        private static void WriteAnsi(byte[] dst, string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s ?? "");
            Array.Copy(bytes, dst, Math.Min(bytes.Length, dst.Length - 1));
        }

        // ═══════════════════════════════════════════════════════════
        // 内部数据结构
        // ═══════════════════════════════════════════════════════════

        private sealed class CameraSession
        {
            public int UserId;
            public int AlarmHandle;
            public int DeviceId;
            public string DeviceCode;
            public CameraConfigEntity Config;
        }

        private sealed class PlateWorkItem
        {
            public int DeviceId;
            public string DeviceCode;
            public CameraConfigEntity Config;
            public CHCNetSDK.NET_ITS_PLATE_RESULT Plate;
            public List<(byte type, byte[] data)> Pictures;
            public DateTime ReceivedAt;
        }
    }
}
