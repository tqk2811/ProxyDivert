using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Configuration.Enums;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Engine.Models;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Extensions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.WinDivert.Inspection.Extensions;
using TqkLibrary.WinDivert.Logging;
using TqkLibrary.WinDivert.ProcessControl;
using TqkLibrary.WinDivert.Redirect;
using TqkLibrary.WinDivert.Redirect.Enums;
using TqkLibrary.WinDivert.Redirect.Models;

namespace ProxyDivert.Core.Engine;

// The whole tool, minus the window.
//
// Shape (see docs/Plan-vi.md §3.1): ONE ProcessRedirector tracks every matched pid; the routing
// decision is made per CONNECTION, not per packet, because the domain name only becomes known
// after the TCP handshake (SNI). "Direct" therefore also goes through the relay — that costs one
// extra copy and buys byte counters, logging, and rule changes that apply without re-attaching.
//
// Threading: Start/Stop/ApplyConfig are expected from the UI thread; connection handlers run on
// relay threads. The resolver is swapped atomically (a whole new instance per config change), so a
// connection being routed never sees a half-applied edit.
public sealed class RedirectEngine : IDisposable
{
    // How long a connection may stay silent before routing gives up on reading a name from it.
    // Protocols where the SERVER speaks first (SMTP, FTP, SSH) would otherwise stall here; they
    // fall back to reverse DNS or to plain IP routing.
    private static readonly TimeSpan HostPeekTimeout = TimeSpan.FromSeconds(3);

    private readonly ILoggerFactory? _loggerFactory;
    private readonly object _stateLock = new object();

    private RedirectLogger _log = RedirectLogger.Null;
    private AppConfig _config = AppConfig.CreateDefault();
    private RoutingPolicyResolver _resolver;
    private OutboundSourceFactory? _outboundFactory;
    private ProcessWatcher? _watcher;
    private ProcessRedirector? _redirector;
    private UdpProxyForwarder? _udpForwarder;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<uint, ProcessTreeMonitor> _treeMonitors = new Dictionary<uint, ProcessTreeMonitor>();

    public ConnectionTracker Connections { get; } = new ConnectionTracker();

    public bool IsRunning { get; private set; }

    /// <summary>Diagnostic stream of the running engine. Null until Start().</summary>
    public RedirectLogger Logger => _log;

    /// <summary>Processes currently under redirection.</summary>
    public IReadOnlyCollection<TrackedProcess> TrackedProcesses
        => _watcher?.Tracked ?? Array.Empty<TrackedProcess>();

    public event Action<TrackedProcess>? ProcessAttached;
    public event Action<TrackedProcess>? ProcessDetached;

    public RedirectEngine(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _resolver = BuildResolver(_config, new Dictionary<uint, Guid>());
    }

    public void Start(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        lock (_stateLock)
        {
            if (IsRunning) throw new InvalidOperationException("Engine already running");

            _config = config;
            _log = new RedirectLogger(_loggerFactory, config.DiagnosticLogPath);
            _cts = new CancellationTokenSource();
            _outboundFactory = new OutboundSourceFactory(_loggerFactory);
            _resolver = BuildResolver(config, new Dictionary<uint, Guid>());

            var options = new RedirectOptions
            {
                // Start with an empty scope: pids arrive from the process watcher.
                ProcessId = 0,
                Protocols = RedirectProtocol.All,
                Logger = _log,
                BlockIpv6 = config.BlockIpv6,
                EnableDnsSniff = true,
                EnableSecureDns = config.Dns.Mode == DnsMode.DnsOverHttps,
                DohEndpoint = ParseDohEndpoint(config.Dns.DohEndpoint),
                TcpConnectionHandler = HandleTcpAsync,
                UdpDatagramHandler = HandleUdpDatagram,
            };

            _redirector = new ProcessRedirector(options);
            _redirector.Start();
            _udpForwarder = new UdpProxyForwarder(_redirector, _log, _cts.Token);

            _watcher = new ProcessWatcher(_log);
            _watcher.ProcessAttached += OnProcessAttached;
            _watcher.ProcessDetached += OnProcessDetached;
            _watcher.Start(config.ProcessRules);

            IsRunning = true;
            _log.Log("ENG", $"Engine started, relay tcp={_redirector.TcpRelayPort} udp={_redirector.UdpRelayPort}");
        }
    }

    // Applies an edited configuration without dropping the redirector: rules, outbounds and DNS
    // preferences take effect on the NEXT connection. Options that live in the WinDivert handles
    // (IPv6 blocking, DoH) need a restart — the UI says so rather than silently ignoring them.
    public void ApplyConfig(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        lock (_stateLock)
        {
            _config = config;
            if (!IsRunning) return;

            _outboundFactory?.InvalidateAll();
            _watcher?.ApplyRules(config.ProcessRules);
            RebuildResolver();
            _log.Log("ENG", "Configuration applied");
        }
    }

    /// <summary>
    /// Runs one process-discovery pass immediately instead of waiting for the next event or poll.
    /// The "launch suspended" flow needs this: the process must be adopted while it is still
    /// frozen, otherwise its first connection is out before the redirect attaches.
    /// </summary>
    public void ForceProcessScan() => _watcher?.ScanOnce();

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!IsRunning) return;
            IsRunning = false;

            try { _cts?.Cancel(); } catch { }

            foreach (var kv in _treeMonitors) kv.Value.Dispose();
            _treeMonitors.Clear();

            if (_watcher != null)
            {
                _watcher.ProcessAttached -= OnProcessAttached;
                _watcher.ProcessDetached -= OnProcessDetached;
                _watcher.Dispose();
                _watcher = null;
            }

            _udpForwarder?.Dispose();
            _udpForwarder = null;

            _redirector?.Dispose();
            _redirector = null;

            _outboundFactory?.Dispose();
            _outboundFactory = null;

            _log.Log("ENG", "Engine stopped");
            _log.Dispose();
            _log = RedirectLogger.Null;

            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---- process scope ----------------------------------------------------------------------

    private void OnProcessAttached(TrackedProcess process)
    {
        try
        {
            _redirector?.AddTrackedProcessId(process.ProcessId);
            RebuildResolver();

            // WMI already reports parent/child, so the poller is only needed when WMI is
            // unavailable — running both would double the work for nothing.
            if (process.MatchedRule?.IncludeChildren == true && _watcher?.IsUsingWmi == false)
                StartTreeMonitor(process.ProcessId);
        }
        catch (Exception ex)
        {
            _log.Log("ENG", $"attach pid={process.ProcessId} failed: {ex.GetType().Name}: {ex.Message}");
        }
        ProcessAttached?.Invoke(process);
    }

    private void OnProcessDetached(TrackedProcess process)
    {
        try
        {
            _redirector?.RemoveTrackedProcessId(process.ProcessId);
            RebuildResolver();
            StopTreeMonitor(process.ProcessId);
        }
        catch (Exception ex)
        {
            _log.Log("ENG", $"detach pid={process.ProcessId} failed: {ex.GetType().Name}: {ex.Message}");
        }
        ProcessDetached?.Invoke(process);
    }

    private void StartTreeMonitor(uint rootPid)
    {
        lock (_treeMonitors)
        {
            if (_treeMonitors.ContainsKey(rootPid)) return;
            var monitor = new ProcessTreeMonitor(rootPid, logger: _log);
            monitor.ChildSpawned += (childPid, parentPid) => _watcher?.AttachChild(childPid, parentPid);
            monitor.Start();
            _treeMonitors[rootPid] = monitor;
        }
    }

    private void StopTreeMonitor(uint rootPid)
    {
        lock (_treeMonitors)
        {
            if (!_treeMonitors.TryGetValue(rootPid, out ProcessTreeMonitor? monitor)) return;
            _treeMonitors.Remove(rootPid);
            monitor.Dispose();
        }
    }

    private void RebuildResolver()
    {
        IReadOnlyDictionary<uint, Guid> policyMap = _watcher?.BuildPolicyMap() ?? new Dictionary<uint, Guid>();
        _resolver = BuildResolver(_config, policyMap);
    }

    private static RoutingPolicyResolver BuildResolver(AppConfig config, IReadOnlyDictionary<uint, Guid> policyMap)
        => new RoutingPolicyResolver(config.Policies, config.Outbounds, policyMap);

    private static Uri ParseDohEndpoint(string? raw)
        => Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) ? uri : new Uri("https://1.1.1.1/dns-query");

    // ---- TCP --------------------------------------------------------------------------------

    private async Task HandleTcpAsync(RedirectedTcpConnection connection, CancellationToken ct)
    {
        string processName = _watcher != null && _watcher.TryGetTracked(connection.ProcessId, out TrackedProcess? tracked)
            ? tracked!.Name
            : $"pid {connection.ProcessId}";

        var info = new ConnectionInfo(
            connection.ProcessId, processName, connection.OriginalDestination, connection.Statistics);
        Connections.Open(info);

        try
        {
            // Name first: SNI / Host header, then whatever DNS taught us about this IP.
            string? host = await connection
                .TryPeekHostNameAsync(_redirector?.ReverseDns, HostPeekTimeout, ct)
                .ConfigureAwait(false);
            info.Host = host;

            var target = new RouteTarget(
                connection.ProcessId,
                connection.OriginalDestination.Address,
                connection.OriginalDestination.Port,
                host);

            RouteDecision decision = _resolver.Resolve(target);
            info.OutboundName = decision.Outbound.Name;
            info.RouteReason = decision.Reason;
            Connections.Update(info);

            _log.Log("ENG", $"tcp pid={connection.ProcessId} {target} -> {decision}");

            if (decision.Outbound.Kind == OutboundKind.Block)
            {
                info.Error = "blocked by rule";
                return;
            }

            await TunnelAsync(connection, decision.Outbound, host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine stopping, or the connection was cancelled — nothing to report.
        }
        catch (Exception ex)
        {
            info.Error = $"{ex.GetType().Name}: {ex.Message}";
            _log.Log("ENG", $"tcp pid={connection.ProcessId} -> {connection.OriginalDestination} failed: {info.Error}");
        }
        finally
        {
            Connections.Close(info);
        }
    }

    private async Task TunnelAsync(RedirectedTcpConnection connection, Outbound outbound, string? host, CancellationToken ct)
    {
        IProxySource source = _outboundFactory!.GetOrCreate(outbound);
        IConnectSource? tunnel = null;
        Guid tunnelId = Guid.NewGuid();
        try
        {
            tunnel = await source.GetConnectSourceAsync(tunnelId, ct).ConfigureAwait(false);

            // Through a proxy, hand over the HOST NAME when we have one: the proxy then resolves
            // it on its own side (remote DNS), so the destination is never leaked to the local
            // resolver and CDN answers stay correct for the proxy's location.
            // Going direct, use the IP the process itself chose — re-resolving could pick a
            // different server than the one the application decided on.
            string targetHost = outbound.Kind == OutboundKind.Direct || string.IsNullOrEmpty(host)
                ? connection.OriginalDestination.Address.ToString()
                : host!;

            var targetUri = new UriBuilder("tcp", targetHost, connection.OriginalDestination.Port).Uri;
            await tunnel.ConnectAsync(targetUri, ct).ConfigureAwait(false);

            await tunnel.ForwardAsync(
                connection.ClientStream, tunnelId, _loggerFactory,
                clientName: $"pid{connection.ProcessId}", proxyName: outbound.Name,
                cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            try { tunnel?.Dispose(); } catch { }
        }
    }

    // ---- UDP --------------------------------------------------------------------------------

    // Returning the payload lets the relay send it out directly; returning null means "handled or
    // dropped — do not send". Anything that cannot be tunnelled is dropped rather than leaked.
    private byte[]? HandleUdpDatagram(RedirectedUdpDatagram datagram, CancellationToken ct)
    {
        try
        {
            string? host = _redirector?.ReverseDns.Resolve(datagram.OriginalDestination.Address);
            var target = new RouteTarget(
                datagram.ProcessId,
                datagram.OriginalDestination.Address,
                datagram.OriginalDestination.Port,
                host,
                isUdp: true);

            RouteDecision decision = _resolver.ResolveUdp(target);

            switch (decision.Outbound.Kind)
            {
                case OutboundKind.Block:
                    return null;

                case OutboundKind.Direct:
                    return datagram.Payload;

                default:
                {
                    IProxySource source = _outboundFactory!.GetOrCreate(decision.Outbound);
                    bool queued = _udpForwarder!.Send(
                        decision.Outbound.Id, source,
                        (ushort)datagram.OriginalSource.Port,
                        datagram.OriginalDestination,
                        datagram.Payload);
                    if (!queued)
                        _log.Log("ENG", $"udp pid={datagram.ProcessId} -> {datagram.OriginalDestination} dropped (tunnel not ready)");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Log("ENG", $"udp pid={datagram.ProcessId} routing failed, dropping: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---- outbound testing --------------------------------------------------------------------

    /// <summary>
    /// Opens a throwaway tunnel through an outbound to check that it works, without touching the
    /// instance live traffic uses. Returns null on success, or the failure description.
    /// </summary>
    public static async Task<string?> TestOutboundAsync(
        Outbound outbound, string testHost = "example.com", int testPort = 80,
        ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        if (outbound.Kind == OutboundKind.Block) return "Block never connects anywhere.";

        using var factory = new OutboundSourceFactory(loggerFactory);
        IConnectSource? tunnel = null;
        try
        {
            IProxySource source = factory.Create(outbound);
            tunnel = await source.GetConnectSourceAsync(Guid.NewGuid(), ct).ConfigureAwait(false);
            await tunnel.ConnectAsync(new UriBuilder("tcp", testHost, testPort).Uri, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            try { tunnel?.Dispose(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
