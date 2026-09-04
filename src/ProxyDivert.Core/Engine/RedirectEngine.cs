using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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
using TqkLibrary.WinDivert.Flow.Models;
using TqkLibrary.WinDivert.Inspection.Interfaces;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;
using TqkLibrary.WinDivert.Redirect;
using TqkLibrary.WinDivert.Redirect.Interfaces;
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

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RedirectEngine> _logger;
    private readonly IProcessRedirectorFactory _redirectorFactory;
    private readonly IProcessTreeMonitorFactory _treeMonitorFactory;
    private readonly IHostNameInspector _hostNameInspector;
    private readonly object _stateLock = new object();
    // What we have learned about which outbounds can actually reach IPv6. Lives across Start/Stop
    // because it describes the proxies, not the run.
    private readonly OutboundIpv6Capability _ipv6Capability = new OutboundIpv6Capability();

    private AppConfig _config = AppConfig.CreateDefault();
    private RoutingPolicyResolver _resolver;
    private OutboundSourceFactory? _outboundFactory;
    private ProcessWatcher? _watcher;
    private IProcessRedirector? _redirector;
    private IConnectionHostNameResolver? _hostNames;
    private UdpProxyForwarder? _udpForwarder;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<uint, IProcessTreeMonitor> _treeMonitors = new Dictionary<uint, IProcessTreeMonitor>();

    public ConnectionTracker Connections { get; } = new ConnectionTracker();

    public bool IsRunning { get; private set; }

    /// <summary>Processes currently under redirection.</summary>
    public IReadOnlyCollection<TrackedProcess> TrackedProcesses
        => _watcher?.Tracked ?? Array.Empty<TrackedProcess>();

    public event Action<TrackedProcess>? ProcessAttached;
    public event Action<TrackedProcess>? ProcessDetached;

    public RedirectEngine(
        IProcessRedirectorFactory redirectorFactory,
        IProcessTreeMonitorFactory treeMonitorFactory,
        IHostNameInspector hostNameInspector,
        ILoggerFactory loggerFactory)
    {
        _redirectorFactory = redirectorFactory ?? throw new ArgumentNullException(nameof(redirectorFactory));
        _treeMonitorFactory = treeMonitorFactory ?? throw new ArgumentNullException(nameof(treeMonitorFactory));
        _hostNameInspector = hostNameInspector ?? throw new ArgumentNullException(nameof(hostNameInspector));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<RedirectEngine>();
        _resolver = BuildResolver(_config, new Dictionary<uint, Guid>());
    }

    public void Start(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        lock (_stateLock)
        {
            if (IsRunning) throw new InvalidOperationException("Engine already running");

            _config = config;

            _cts = new CancellationTokenSource();
            _outboundFactory = new OutboundSourceFactory(_loggerFactory, config.WireProxyPath);
            _resolver = BuildResolver(config, new Dictionary<uint, Guid>());

            var options = new RedirectOptions
            {
                // Start with an empty scope: pids arrive from the process watcher.
                ProcessId = 0,
                Protocols = RedirectProtocol.All,

                Ipv6Mode = config.Ipv6,
                EnableDnsSniff = true,
                EnableSecureDns = config.Dns.Mode == DnsMode.DnsOverHttps,
                DohEndpoint = ParseDohEndpoint(config.Dns.DohEndpoint),
                TcpConnectionHandler = HandleTcpAsync,
                UdpDatagramHandler = HandleUdpDatagram,
            };

            _redirector = _redirectorFactory.Create(options);
            _redirector.Start();
            _hostNames = new ConnectionHostNameResolver(_hostNameInspector, _redirector.ReverseDns);
            _udpForwarder = new UdpProxyForwarder(_redirector, _loggerFactory.CreateLogger<UdpProxyForwarder>(), _cts.Token);

            _watcher = new ProcessWatcher(_loggerFactory.CreateLogger<ProcessWatcher>());
            _watcher.ProcessAttached += OnProcessAttached;
            _watcher.ProcessDetached += OnProcessDetached;
            _watcher.Start(config.ProcessRules);

            IsRunning = true;
            _logger.LogInformation(
                "engine started; relay tcp={Tcp} udp={Udp} tcpV6={TcpV6} udpV6={UdpV6}, ipv6={Ipv6Mode}",
                _redirector.TcpRelayPort, _redirector.UdpRelayPort,
                _redirector.TcpRelayPortV6, _redirector.UdpRelayPortV6, config.Ipv6);
        }
    }

    // Applies an edited configuration without dropping the redirector: rules, outbounds and DNS
    // preferences take effect on the NEXT connection. Options that live in the WinDivert handles
    // (the IPv6 mode, DoH) need a restart — the UI says so rather than silently ignoring them.
    public void ApplyConfig(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        lock (_stateLock)
        {
            _config = config;
            if (!IsRunning) return;

            _outboundFactory?.InvalidateAll();
            if (_outboundFactory != null) _outboundFactory.WireProxyPath = config.WireProxyPath;
            // The edit may be exactly the fix for what we learned (a VPN that now has an IPv6
            // route, a different proxy behind the same entry), so give every outbound a clean slate.
            _ipv6Capability.ResetAll();
            _watcher?.ApplyRules(config.ProcessRules);
            RebuildResolver();
            _logger.LogInformation("configuration applied");
        }
    }

    /// <summary>
    /// Redirects one specific process (and, by default, whatever it spawns) without a rule
    /// describing it. This is how you redirect "this browser I just launched" instead of every
    /// process that happens to share its file name — the user's own copy included.
    /// </summary>
    public TrackedProcess? AttachProcessId(uint processId, Guid policyId, bool includeChildren = true)
    {
        if (!IsRunning) throw new InvalidOperationException("Engine is not running");

        TrackedProcess? tracked = _watcher?.AttachProcessId(processId, policyId, includeChildren);
        // The tree monitor is normally only needed when WMI is unavailable, but an explicitly
        // attached process is usually one just launched suspended: its children appear within
        // milliseconds of the resume, and the poller is what catches them either way.
        if (tracked != null && includeChildren) StartTreeMonitor(processId);
        return tracked;
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

            _logger.LogInformation("engine stopped");

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
            _logger.LogWarning(ex, "attaching pid={Pid} failed", process.ProcessId);
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
            _logger.LogWarning(ex, "detaching pid={Pid} failed", process.ProcessId);
        }
        ProcessDetached?.Invoke(process);
    }

    private void StartTreeMonitor(uint rootPid)
    {
        lock (_treeMonitors)
        {
            if (_treeMonitors.ContainsKey(rootPid)) return;
            IProcessTreeMonitor monitor = _treeMonitorFactory.Create(rootPid);
            monitor.ChildSpawned += (childPid, parentPid) => _watcher?.AttachChild(childPid, parentPid);
            monitor.Start();
            _treeMonitors[rootPid] = monitor;
        }
    }

    private void StopTreeMonitor(uint rootPid)
    {
        lock (_treeMonitors)
        {
            if (!_treeMonitors.TryGetValue(rootPid, out IProcessTreeMonitor? monitor)) return;
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
            string? host = _hostNames is null
                ? null
                : await _hostNames.TryResolveAsync(connection, HostPeekTimeout, ct).ConfigureAwait(false);
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

            _logger.LogInformation("tcp pid={Pid} {Target} -> {Decision}", connection.ProcessId, target, decision);

            if (decision.Outbound.Kind == OutboundKind.Block)
            {
                info.Error = "blocked by rule";
                return;
            }

            await TunnelAsync(connection, decision.Outbound, host, info, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine stopping, or the connection was cancelled — nothing to report.
        }
        catch (Exception ex)
        {
            info.Error = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogWarning(ex, "tcp pid={Pid} -> {Destination} failed", connection.ProcessId, connection.OriginalDestination);
        }
        finally
        {
            Connections.Close(info);
        }
    }

    private async Task TunnelAsync(
        RedirectedTcpConnection connection, Outbound outbound, string? host, ConnectionInfo info, CancellationToken ct)
    {
        IPEndPoint destination = connection.OriginalDestination;
        bool isIpv6 = destination.Address.AddressFamily == AddressFamily.InterNetworkV6;

        // Through a proxy, hand over the HOST NAME when we have one: the proxy then resolves it on
        // its own side (remote DNS), so the destination is never leaked to the local resolver and
        // CDN answers stay correct for the proxy's location. That is also the IPv6 fallback that
        // costs nothing — a name lets an outbound without an IPv6 route pick the A record itself.
        // Going direct, use the IP the process itself chose: re-resolving could pick a different
        // server than the one the application decided on.
        bool byName = outbound.Kind != OutboundKind.Direct && !string.IsNullOrEmpty(host);

        // An IPv6 literal and no name to fall back on: there is no IPv4 address to reach this
        // destination with, so an outbound without an IPv6 route cannot serve it at all. Refusing
        // now — instead of waiting for a timeout — is what lets the application fall back to IPv4
        // on its own (Happy Eyeballs retries the A record within a couple of hundred milliseconds).
        if (isIpv6 && !byName && !_ipv6Capability.AllowsIpv6(outbound))
        {
            info.Error = "outbound has no IPv6 route and the connection carries no host name";
            _logger.LogInformation(
                "tcp pid={Pid} -> {Destination} refused: {Outbound} has no IPv6 route and the connection "
                + "carries no host name to resolve to IPv4, so the application should retry over IPv4",
                connection.ProcessId, destination, outbound.Name);
            return;
        }

        IProxySource source = _outboundFactory!.GetOrCreate(outbound);
        IConnectSource? tunnel = null;
        Guid tunnelId = Guid.NewGuid();
        try
        {
            tunnel = await source.GetConnectSourceAsync(tunnelId, ct).ConfigureAwait(false);

            string targetHost = byName ? host! : destination.Address.ToString();
            // UriBuilder brackets an IPv6 literal for us ("tcp://[2606:4700::1111]:443"), which is
            // what the SOCKS5/HTTP address parsers expect to see.
            var targetUri = new UriBuilder("tcp", targetHost, destination.Port).Uri;

            try
            {
                await tunnel.ConnectAsync(targetUri, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (isIpv6 && !byName && !ct.IsCancellationRequested)
            {
                // First IPv6 destination this outbound failed to reach. Nothing says the far side
                // is v4-only rather than that host being down, but assuming the cheaper of the two
                // is right: later IPv6 connections are refused immediately instead of stalling,
                // and named ones keep working because the outbound resolves them itself.
                NoteIpv6Failure(outbound, destination, ex);
                throw;
            }

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

    // Direct is the machine's own stack: if it had no IPv6 the process could not have opened an
    // IPv6 connection in the first place, so one unreachable destination says nothing about it.
    private void NoteIpv6Failure(Outbound outbound, IPEndPoint destination, Exception ex)
    {
        if (outbound.Kind == OutboundKind.Direct) return;
        if (!_ipv6Capability.RecordIpv6Failure(outbound)) return;

        _outboundFactory?.SetIpv6Support(outbound.Id, false);
        _logger.LogInformation(ex,
            "outbound {Outbound} marked IPv4-only after {Destination} failed. Later IPv6 destinations go "
            + "out over IPv4, by name where one is known; set Ipv6Support=Enabled to override",
            outbound.Name, destination);
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
                    bool isIpv6 = datagram.OriginalDestination.AddressFamily == AddressFamily.InterNetworkV6;
                    // A UDP datagram carries no name to fall back on, so an outbound without an
                    // IPv6 route has nothing to send it over. Dropping is the safe answer: letting
                    // it out direct would expose the real address.
                    if (isIpv6 && !_ipv6Capability.AllowsIpv6(decision.Outbound))
                    {
                        _logger.LogDebug(
                            "udp pid={Pid} -> {Destination} dropped: {Outbound} has no IPv6 route",
                            datagram.ProcessId, datagram.OriginalDestination, decision.Outbound.Name);
                        return null;
                    }

                    IProxySource source = _outboundFactory!.GetOrCreate(decision.Outbound);
                    bool queued = _udpForwarder!.Send(
                        decision.Outbound.Id, source,
                        (ushort)datagram.OriginalSource.Port,
                        datagram.OriginalDestination,
                        datagram.Payload,
                        isIpv6);
                    if (!queued)
                        _logger.LogDebug("udp pid={Pid} -> {Destination} dropped, the tunnel is not ready yet", datagram.ProcessId, datagram.OriginalDestination);
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "udp pid={Pid} routing failed, dropping the datagram", datagram.ProcessId);
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
        ILoggerFactory? loggerFactory = null, string? wireProxyPath = null, CancellationToken ct = default)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        if (outbound.Kind == OutboundKind.Block) return "Block never connects anywhere.";

        // A VPN test starts its own wireproxy subprocess and tears it down with the factory, so it
        // never disturbs a tunnel that live traffic is already using.
        using var factory = new OutboundSourceFactory(loggerFactory, wireProxyPath);
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
