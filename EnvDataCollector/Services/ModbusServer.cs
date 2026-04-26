using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EnvDataCollector.Data.Repositories;
using NLog;

namespace EnvDataCollector.Services
{
    /// <summary>
    /// 极简 Modbus TCP Slave。监听端口接受客户端连接，每个客户端独立线程读 MBAP+PDU，
    /// 仅支持功能码 01/02（read coils/discrete）和 03/04（read holding/input registers），
    /// 地址 0x0000 起（对应"00001"/"40001"）。
    /// 6 个 Coil  ：AppRunning / HeartbeatBit / OpcUaAnyDisconnected / CameraAnyOffline / PushHasFailed / PushHasPending
    /// 6 个 Holding：HeartbeatCounter / OpcUaDisconnectedCount / CameraOfflineCount / PushFailedCount / PushPendingCount / PushOldestPendingMin
    /// 后台 1s tick 从 OpcUa/Cam/Outbox 拉数据更新。
    /// 启用与否由 AppSetting.ModbusEnabled 控制（默认 0）。
    /// </summary>
    public sealed class ModbusServer
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        // 索引 ↔ 字段（与 ModbusPanel.CoilNames/RegNames 对齐）
        public const int COIL_AppRunning           = 0;
        public const int COIL_HeartbeatBit         = 1;
        public const int COIL_OpcUaAnyDisconnected = 2;
        public const int COIL_CameraAnyOffline     = 3;
        public const int COIL_PushHasFailed        = 4;
        public const int COIL_PushHasPending       = 5;
        public const int COIL_COUNT                = 6;

        public const int REG_HeartbeatCounter      = 0;
        public const int REG_OpcUaDisconnectedCount= 1;
        public const int REG_CameraOfflineCount    = 2;
        public const int REG_PushFailedCount       = 3;
        public const int REG_PushPendingCount      = 4;
        public const int REG_PushOldestPendingMin  = 5;
        public const int REG_COUNT                 = 6;

        private readonly bool[]   _coils    = new bool[COIL_COUNT];
        private readonly ushort[] _registers = new ushort[REG_COUNT];
        private readonly object   _dataLock = new();

        private readonly OutboxRepository _outbox = new();
        private readonly CameraConfigRepository _camRepo = new();
        private readonly AppSettingRepository _settings = new();

        private CancellationTokenSource _cts;
        private Task _acceptLoop;
        private TcpListener _listener;
        private System.Threading.Timer _tickTimer;
        private MainStatusProvider _status;

        public bool Running { get; private set; }
        public string ListenInfo { get; private set; }   // "0.0.0.0:1502" 之类，UI 显示用

        /// <summary>UI 用的快照（深拷贝）。</summary>
        public (bool[] coils, ushort[] regs) Snapshot()
        {
            lock (_dataLock)
                return ((bool[])_coils.Clone(), (ushort[])_registers.Clone());
        }

        /// <summary>启动 Modbus Server。需要 status 提供器从 OPC UA / Cam 拿数据。</summary>
        public void Start(MainStatusProvider status)
        {
            if (Running) return;
            _status = status ?? throw new ArgumentNullException(nameof(status));

            if (_settings.Get<int>(SK.ModbusEnabled, 0) != 1)
            {
                Log.Info("Modbus 未启用（AppSetting.ModbusEnabled=0）");
                return;
            }

            string ip = _settings.Get(SK.ModbusListenIp, "0.0.0.0");
            int port  = _settings.Get<int>(SK.ModbusListenPort, 1502);
            IPAddress ipAddr;
            if (!IPAddress.TryParse(ip, out ipAddr)) ipAddr = IPAddress.Any;

            try
            {
                _listener = new TcpListener(ipAddr, port);
                _listener.Start();
                ListenInfo = $"{ipAddr}:{port}";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Modbus 监听 {0}:{1} 失败", ip, port);
                ListenInfo = "(启动失败)";
                return;
            }

            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
            _tickTimer = new System.Threading.Timer(_ => SafeTick(), null, 0, 1000);
            // AppRunning 立即置 1
            lock (_dataLock) _coils[COIL_AppRunning] = true;

            Running = true;
            Log.Info("ModbusServer 已启动，监听 {0}", ListenInfo);
        }

        public void Stop()
        {
            if (!Running) return;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _tickTimer?.Dispose(); } catch { }
            try { _acceptLoop?.Wait(500); } catch { }
            _cts = null; _listener = null; _tickTimer = null; _acceptLoop = null;
            Running = false;
            Log.Info("ModbusServer 已停止");
        }

        // ═══════════════════════════════════════════════════════════
        // 1s tick：刷新数据点
        // ═══════════════════════════════════════════════════════════

        private void SafeTick()
        {
            try { Tick(); } catch (Exception ex) { Log.Debug(ex, "Modbus.Tick 异常"); }
        }

        private void Tick()
        {
            int opcDisc  = _status?.OpcUaDisconnectedCount() ?? 0;
            int camTotal = 0;
            try { camTotal = _camRepo.GetAll(true).Count(); } catch { }
            int camActive = _status?.CameraActiveCount() ?? 0;
            int camOff = Math.Max(0, camTotal - camActive);

            long failed = 0, pending = 0;
            int oldestMin = 0;
            try
            {
                var (f, p) = _outbox.GetCounts();
                failed = f; pending = p;
                var oldest = _outbox.GetOldestPendingTime();
                if (oldest.HasValue)
                    oldestMin = (int)Math.Min(ushort.MaxValue, (DateTime.Now - oldest.Value).TotalMinutes);
            }
            catch { }

            string hbMode = _settings.Get(SK.ModbusHeartbeat, "Counter");
            lock (_dataLock)
            {
                _coils[COIL_AppRunning]           = true;
                _coils[COIL_OpcUaAnyDisconnected] = opcDisc > 0;
                _coils[COIL_CameraAnyOffline]     = camOff  > 0;
                _coils[COIL_PushHasFailed]        = failed  > 0;
                _coils[COIL_PushHasPending]       = pending > 0;

                _registers[REG_OpcUaDisconnectedCount] = (ushort)Math.Min(ushort.MaxValue, opcDisc);
                _registers[REG_CameraOfflineCount]     = (ushort)Math.Min(ushort.MaxValue, camOff);
                _registers[REG_PushFailedCount]        = (ushort)Math.Min(ushort.MaxValue, failed);
                _registers[REG_PushPendingCount]       = (ushort)Math.Min(ushort.MaxValue, pending);
                _registers[REG_PushOldestPendingMin]   = (ushort)oldestMin;

                if (string.Equals(hbMode, "Bit", StringComparison.OrdinalIgnoreCase))
                {
                    _coils[COIL_HeartbeatBit] = !_coils[COIL_HeartbeatBit];
                }
                else
                {
                    _coils[COIL_HeartbeatBit] = !_coils[COIL_HeartbeatBit];   // bit 也跟着翻，让两个模式都能用
                    _registers[REG_HeartbeatCounter] = (ushort)((_registers[REG_HeartbeatCounter] + 1) & 0xFFFF);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // TCP 接受循环 + Modbus 报文处理
        // ═══════════════════════════════════════════════════════════

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient cli;
                try { cli = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                _ = Task.Run(() => HandleClient(cli, ct), ct);
            }
        }

        private async Task HandleClient(TcpClient cli, CancellationToken ct)
        {
            string remote = cli.Client?.RemoteEndPoint?.ToString() ?? "?";
            try
            {
                using (cli)
                using (var stream = cli.GetStream())
                {
                    var hdr = new byte[7];
                    while (!ct.IsCancellationRequested)
                    {
                        int read = await ReadExactlyAsync(stream, hdr, 0, 7, ct);
                        if (read < 7) return;

                        ushort txnId  = (ushort)((hdr[0] << 8) | hdr[1]);
                        ushort protoId= (ushort)((hdr[2] << 8) | hdr[3]);
                        ushort length = (ushort)((hdr[4] << 8) | hdr[5]);
                        byte unitId   = hdr[6];

                        if (protoId != 0 || length < 2 || length > 260) return;
                        int pduLen = length - 1;
                        var pdu = new byte[pduLen];
                        if (await ReadExactlyAsync(stream, pdu, 0, pduLen, ct) < pduLen) return;

                        byte[] respPdu = HandlePdu(pdu);
                        var resp = BuildMbap(txnId, unitId, respPdu);
                        await stream.WriteAsync(resp, 0, resp.Length, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) { Log.Debug(ex, "Modbus client {0} 异常", remote); }
        }

        private byte[] HandlePdu(byte[] pdu)
        {
            byte fc = pdu[0];
            try
            {
                switch (fc)
                {
                    case 0x01:   // Read Coils
                    case 0x02:   // Read Discrete Inputs（这里 coil 与 discrete 共用同一组数据）
                        return ReadBits(fc, pdu);
                    case 0x03:   // Read Holding Registers
                    case 0x04:   // Read Input Registers
                        return ReadWords(fc, pdu);
                    default:
                        return new byte[] { (byte)(fc | 0x80), 0x01 };   // Illegal function
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "PDU 解析异常 fc=0x{0:X2}", fc);
                return new byte[] { (byte)(fc | 0x80), 0x04 };   // Slave device failure
            }
        }

        private byte[] ReadBits(byte fc, byte[] pdu)
        {
            if (pdu.Length < 5) return new byte[] { (byte)(fc | 0x80), 0x03 };
            int addr = (pdu[1] << 8) | pdu[2];
            int qty  = (pdu[3] << 8) | pdu[4];
            if (qty < 1 || qty > 2000) return new byte[] { (byte)(fc | 0x80), 0x03 };
            if (addr + qty > COIL_COUNT) return new byte[] { (byte)(fc | 0x80), 0x02 };

            int byteCount = (qty + 7) / 8;
            var resp = new byte[2 + byteCount];
            resp[0] = fc;
            resp[1] = (byte)byteCount;
            lock (_dataLock)
            {
                for (int i = 0; i < qty; i++)
                {
                    if (_coils[addr + i])
                        resp[2 + (i / 8)] |= (byte)(1 << (i % 8));
                }
            }
            return resp;
        }

        private byte[] ReadWords(byte fc, byte[] pdu)
        {
            if (pdu.Length < 5) return new byte[] { (byte)(fc | 0x80), 0x03 };
            int addr = (pdu[1] << 8) | pdu[2];
            int qty  = (pdu[3] << 8) | pdu[4];
            if (qty < 1 || qty > 125) return new byte[] { (byte)(fc | 0x80), 0x03 };
            if (addr + qty > REG_COUNT) return new byte[] { (byte)(fc | 0x80), 0x02 };

            int byteCount = qty * 2;
            var resp = new byte[2 + byteCount];
            resp[0] = fc;
            resp[1] = (byte)byteCount;
            lock (_dataLock)
            {
                for (int i = 0; i < qty; i++)
                {
                    ushort v = _registers[addr + i];
                    resp[2 + i * 2]     = (byte)(v >> 8);
                    resp[3 + i * 2]     = (byte)(v & 0xFF);
                }
            }
            return resp;
        }

        private static byte[] BuildMbap(ushort txnId, byte unitId, byte[] pdu)
        {
            int len = pdu.Length + 1;   // unitId + pdu
            var buf = new byte[7 + pdu.Length];
            buf[0] = (byte)(txnId >> 8);
            buf[1] = (byte)(txnId & 0xFF);
            buf[2] = 0; buf[3] = 0;     // protocol id = 0
            buf[4] = (byte)(len >> 8);
            buf[5] = (byte)(len & 0xFF);
            buf[6] = unitId;
            Buffer.BlockCopy(pdu, 0, buf, 7, pdu.Length);
            return buf;
        }

        private static async Task<int> ReadExactlyAsync(NetworkStream s, byte[] buf, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await s.ReadAsync(buf, offset + read, count - read, ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            return read;
        }

        /// <summary>外部状态供给接口（避免直接耦合 MainForm）。</summary>
        public interface MainStatusProvider
        {
            int OpcUaDisconnectedCount();
            int CameraActiveCount();
        }
    }
}
