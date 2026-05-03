using EnvDataCollector.Data.Repositories;
using EnvDataCollector.Models;
using NLog;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EnvDataCollector.Services
{
    public class OpcUaService : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly OpcUaServerRepository _srvRepo = new();
        private readonly DeviceRepository _devRepo = new();
        private readonly DeviceVariableRepository _varRepo = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<int, ServerCtx> _ctxs = new();

        // ★ KeepAlive 配置
        private const int KeepAliveIntervalMs = 5000;   // 5秒一次探活
        private const int KeepAliveMaxMissed = 3;      // 连续 3 次失败则断开重连

        public event Action<int, string, object, DateTime, bool> OnValueChanged;
        public event Action<int, bool> OnSessionState;

        public void Start()
        {
            foreach (var s in _srvRepo.GetAll().Where(x => x.Enabled == 1))
                Task.Run(() => RunServer(s, _cts.Token));
        }

        private async Task RunServer(OpcUaServerEntity srv, CancellationToken ct)
        {
            int delay = 3000;
            while (!ct.IsCancellationRequested)
            {
                // ★ 每轮重新从库读取最新配置（支持运行时修改）
                var latest = _srvRepo.GetById(srv.Id);
                if (latest != null) srv = latest;
                if (srv.Enabled == 0)
                {
                    Log.Info($"[OpcUA] {srv.Name} 已禁用，停止重连");
                    return;
                }

                ServerCtx ctx = null;
                try
                {
                    Log.Info($"[OpcUA] 连接 {srv.Name} → {srv.EndpointUrl}");
                    ctx = await ConnectAsync(srv);
                    _ctxs[srv.Id] = ctx;
                    OnSessionState?.Invoke(srv.Id, true);
                    delay = 3000;

                    await SubscribeAsync(ctx, srv.Id, ct);

                    // ★ 等待断连：KeepAlive 失败会 Close session → Connected=false
                    while (!ct.IsCancellationRequested && ctx.Session.Connected && !ctx.Aborted)
                        await Task.Delay(2000, ct);

                    if (ctx.Aborted)
                        Log.Warn($"[OpcUA] {srv.Name} KeepAlive 连续失败，触发重连");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Log.Warn(ex, $"[OpcUA] {srv.Name} 异常，{delay / 1000}s 后重连");
                }
                finally
                {
                    if (ctx != null) SafeClose(ctx);
                    _ctxs.TryRemove(srv.Id, out _);
                    OnSessionState?.Invoke(srv.Id, false);
                }
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 60_000);
            }
        }

        private async Task<ServerCtx> ConnectAsync(OpcUaServerEntity srv)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string certDir = System.IO.Path.Combine(baseDir, "OpcUaCerts");

            var app = new ApplicationInstance
            {
                ApplicationName = "EnvDataCollector",
                ApplicationType = ApplicationType.Client
            };

            var cfg = new ApplicationConfiguration
            {
                ApplicationName = "EnvDataCollector",
                ApplicationUri = Utils.Format("urn:{0}:EnvDataCollector",
                                      System.Net.Dns.GetHostName()),
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = System.IO.Path.Combine(certDir, "own"),
                        SubjectName = Utils.Format("CN={0}, O=EnvData, DC={1}",
                                          "EnvDataCollector", System.Net.Dns.GetHostName())
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = System.IO.Path.Combine(certDir, "issuers")
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = System.IO.Path.Combine(certDir, "trusted")
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = System.IO.Path.Combine(certDir, "rejected")
                    },
                    AutoAcceptUntrustedCertificates = true
                },
                TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 }
            };

            await cfg.Validate(ApplicationType.Client);
            app.ApplicationConfiguration = cfg;
            bool certOk = await app.CheckApplicationInstanceCertificate(true, 2048);
            if (!certOk) Log.Warn("[OpcUA] 证书检查未通过，尝试继续...");

            cfg.CertificateValidator.CertificateValidation += (s, e) =>
            {
                e.Accept = true;
                Log.Debug($"[OpcUA] 自动接受证书: {e.Certificate.Subject}");
            };

            bool useSec = srv.SecurityMode != "None";
            var ep = CoreClientUtils.SelectEndpoint(srv.EndpointUrl, useSec, 10_000);
            var cep = new ConfiguredEndpoint(null, ep, EndpointConfiguration.Create(cfg));

            IUserIdentity id = srv.AuthType == "UsernamePassword"
                ? new UserIdentity(srv.Username, CryptoHelper.Decrypt(srv.PasswordEnc))
                : new UserIdentity(new AnonymousIdentityToken());

            var session = await Session.Create(cfg, cep, false, srv.Name, 60_000, id, null);

            // ★★★ 关键：启用 KeepAlive 探活 ★★★
            session.KeepAliveInterval = KeepAliveIntervalMs;

            var ctx = new ServerCtx { ServerId = srv.Id, Name = srv.Name, Session = session };

            // ★ KeepAlive 回调：连续失败超阈值则标记断开
            session.KeepAlive += (sess, e) =>
            {
                if (e == null) return;
                if (ServiceResult.IsGood(e.Status))
                {
                    // 探活成功，重置计数
                    ctx.MissedKeepAlives = 0;
                }
                else
                {
                    ctx.MissedKeepAlives++;
                    Log.Warn($"[OpcUA] {srv.Name} KeepAlive 失败 #{ctx.MissedKeepAlives}: {e.Status}");
                    if (ctx.MissedKeepAlives >= KeepAliveMaxMissed)
                    {
                        Log.Error($"[OpcUA] {srv.Name} 连续 {KeepAliveMaxMissed} 次 KeepAlive 失败，强制断开");
                        ctx.Aborted = true;
                        try { session.Close(); } catch { }
                    }
                }
            };

            Log.Info($"[OpcUA] {srv.Name} 会话建立 ep={ep.EndpointUrl} keepAlive={KeepAliveIntervalMs}ms");
            return ctx;
        }

        private async Task SubscribeAsync(ServerCtx ctx, int serverId, CancellationToken ct)
        {
            var devices = _devRepo.GetAll(true).Where(d => d.ServerId == serverId).ToList();
            if (!devices.Any()) return;
            var sub = new Subscription(ctx.Session.DefaultSubscription)
            { PublishingInterval = 500, PublishingEnabled = true };
            ctx.Session.AddSubscription(sub);
            sub.Create();
            foreach (var dev in devices)
                foreach (var v in _varRepo.GetByDevice(dev.Id).Where(x => x.Enabled == 1))
                {
                    int did = dev.Id; string role = v.VarRole;
                    var mi = new MonitoredItem(sub.DefaultItem)
                    {
                        StartNodeId = v.NodeId,
                        AttributeId = Attributes.Value,
                        SamplingInterval = v.SamplingMs,
                        DisplayName = v.DisplayName ?? v.NodeId
                    };
                    mi.Notification += (item, _) =>
                    {
                        if (item.LastValue is MonitoredItemNotification n)
                        {
                            bool good = StatusCode.IsGood(n.Value?.StatusCode ?? StatusCodes.Bad);
                            OnValueChanged?.Invoke(did, role, n.Value?.Value,
                                n.Value?.SourceTimestamp.ToLocalTime() ?? DateTime.Now, good);
                        }
                    };
                    sub.AddItem(mi);
                }
            sub.ApplyChanges();
            Log.Info($"[OpcUA] Server {serverId} 订阅 {sub.MonitoredItemCount} 项");
        }

        // ── 变量浏览 ─────────────────────────────────────────────
        public List<OpcNodeInfo> Browse(int serverId, string parentNodeId = null)
        {
            if (!_ctxs.TryGetValue(serverId, out var ctx)) return new();
            var startId = string.IsNullOrEmpty(parentNodeId)
                ? ObjectIds.ObjectsFolder : new NodeId(parentNodeId);
            try
            {
                ctx.Session.Browse(null, null, startId, 0,
                    BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences,
                    true, (uint)(NodeClass.Variable | NodeClass.Object), out _, out var refs);
                var list = new List<OpcNodeInfo>();
                foreach (var r in refs)
                    list.Add(new OpcNodeInfo
                    {
                        NodeId = r.NodeId.ToString(),
                        DisplayName = r.DisplayName.Text,
                        BrowseName = r.BrowseName.Name,
                        IsVariable = r.NodeClass == NodeClass.Variable
                    });
                return list;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Browse 失败 serverId={0}", serverId);
                return new();
            }
        }

        public List<OpcNodeInfo> Search(int serverId, string keyword)
        {
            if (!_ctxs.TryGetValue(serverId, out var ctx)) return new();
            var result = new List<OpcNodeInfo>();
            RecurseBrowse(ctx.Session, ObjectIds.ObjectsFolder,
                keyword?.ToLowerInvariant() ?? "", "", result, 0);
            return result;
        }

        private static void RecurseBrowse(Session session, NodeId nodeId,
            string kw, string path, List<OpcNodeInfo> result, int depth)
        {
            if (depth > 8 || result.Count >= 300) return;
            ReferenceDescriptionCollection refs;
            try
            {
                session.Browse(null, null, nodeId, 0,
                    BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences,
                    true, (uint)(NodeClass.Variable | NodeClass.Object), out _, out refs);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "RecurseBrowse Browse 失败 nodeId={0}", nodeId);
                return;
            }
            foreach (var r in refs)
            {
                string fp = string.IsNullOrEmpty(path)
                    ? r.DisplayName.Text : $"{path}/{r.DisplayName.Text}";
                if (r.NodeClass == NodeClass.Variable)
                {
                    string nid = r.NodeId.ToString().ToLower();
                    string name = r.DisplayName.Text.ToLower();
                    if (string.IsNullOrEmpty(kw) || name.Contains(kw) ||
                        r.BrowseName.Name.ToLower().Contains(kw) || nid.Contains(kw))
                        result.Add(new OpcNodeInfo
                        {
                            NodeId = r.NodeId.ToString(),
                            DisplayName = r.DisplayName.Text,
                            BrowseName = r.BrowseName.Name,
                            BrowsePath = fp,
                            IsVariable = true
                        });
                }
                NodeId childId;
                try { childId = (NodeId)r.NodeId; }
                catch { continue; }
                if (childId == null) continue;
                RecurseBrowse(session, childId, kw, fp, result, depth + 1);
            }
        }

        public (bool ok, string dtype, string error) TestRead(int serverId, string nodeId)
        {
            if (!_ctxs.TryGetValue(serverId, out var ctx))
                return (false, null, "服务器未连接");
            try
            {
                var ids = new ReadValueIdCollection
                    { new ReadValueId { NodeId = new NodeId(nodeId), AttributeId = Attributes.Value } };
                ctx.Session.Read(null, 0, TimestampsToReturn.Both, ids, out var results, out _);
                if (StatusCode.IsBad(results[0].StatusCode))
                    return (false, null, results[0].StatusCode.ToString());
                return (true, results[0].Value?.GetType().Name, null);
            }
            catch (Exception ex) { return (false, null, ex.Message); }
        }

        public void ReadNodeValues(int serverId, List<OpcNodeInfo> nodes)
        {
            if (nodes == null || nodes.Count == 0) return;
            if (!_ctxs.TryGetValue(serverId, out var ctx)) return;
            int batch = 50;
            for (int i = 0; i < nodes.Count; i += batch)
            {
                var slice = nodes.Skip(i).Take(batch).ToList();
                var ids = new ReadValueIdCollection();
                foreach (var n in slice)
                    ids.Add(new ReadValueId
                        { NodeId = new NodeId(n.NodeId), AttributeId = Attributes.Value });
                try
                {
                    ctx.Session.Read(null, 0, TimestampsToReturn.Both, ids, out var results, out _);
                    for (int j = 0; j < slice.Count && j < results.Count; j++)
                    {
                        bool good = StatusCode.IsGood(results[j].StatusCode);
                        slice[j].Quality = good ? "Good" : "Bad";
                        if (good)
                        {
                            slice[j].Value = results[j].Value?.ToString();
                            if (results[j].Value != null)
                                slice[j].DataType = results[j].Value.GetType().Name;
                        }
                    }
                }
                catch { }
            }
        }

        public void ForceReconnect(int serverId)
        {
            if (_ctxs.TryGetValue(serverId, out var ctx))
            {
                ctx.Aborted = true;
                try { ctx.Session?.Close(); } catch { }
            }
        }

        public bool IsConnected(int serverId) =>
            _ctxs.TryGetValue(serverId, out var c) && c.Session?.Connected == true && !c.Aborted;

        public int DisconnectedCount()
        {
            int total = _srvRepo.GetAll().Count(x => x.Enabled == 1);
            return total - _ctxs.Values.Count(c => c.Session?.Connected == true && !c.Aborted);
        }

        private static void SafeClose(ServerCtx ctx)
        {
            try { ctx.Session?.Close(); } catch { }
            try { ctx.Session?.Dispose(); } catch { }
        }

        public void Dispose() { _cts.Cancel(); foreach (var c in _ctxs.Values) SafeClose(c); }

        public class ServerCtx
        {
            public int ServerId { get; set; }
            public string Name { get; set; }
            public Session Session { get; set; }
            public int MissedKeepAlives { get; set; }   // ★ KeepAlive 连续失败计数
            public bool Aborted { get; set; }   // ★ 标记需要断开重连
        }
    }
}
