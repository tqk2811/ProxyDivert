using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn;
using ProxyDivert.Core.Vpn.Models;
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
    private readonly ProcessInventory _inventory;
    private readonly IHostNameInspector _hostNameInspector;
    private readonly OutboundRegistry _outbounds;
    private readonly object _stateLock = new object();
    // Start, Stop and ApplyConfig are mutually exclusive, and each of them now awaits work that
    // must not happen under _stateLock — closing a UDP tunnel, putting a VPN tunnel down, unloading
    // the driver. A monitor cannot be held across an await, so the lock guards only the moment the
    // run's parts are published or taken away, and this keeps the operations themselves from
    // overlapping.
    private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
    // What we have learned about which outbounds can actually reach IPv6. Lives across Start/Stop
    // because it describes the proxies, not the run.
    private readonly OutboundIpv6Capability _ipv6Capability = new OutboundIpv6Capability();

    private AppConfig _config = AppConfig.CreateDefault();

    // What the engine is running, or null while it is stopped. Volatile because it is published and
    // taken away under _stateLock but read without it, from the relay threads: a handler takes the
    // whole run once, at the top, and never works with half of one. See EngineRun.
    private volatile EngineRun? _run;

    public ConnectionTracker Connections { get; } = new ConnectionTracker();

    // The TCP connections being tunnelled right now, so a configuration change reaches them too.
    private readonly LiveTcpConnectionRegistry _liveConnections = new LiveTcpConnectionRegistry();

    // There is nothing else "running" means: the run exists, or it does not.
    public bool IsRunning => _run is not null;

    /// <summary>Processes currently under redirection.</summary>
    public IReadOnlyCollection<TrackedProcess> TrackedProcesses
        => _run?.Tracker.Tracked ?? Array.Empty<TrackedProcess>();

    public event Action<TrackedProcess>? ProcessAttached;
    public event Action<TrackedProcess>? ProcessDetached;

    /// <summary>
    /// Raised once after <see cref="ApplyConfig"/> has taken effect on a running engine. A rule edit
    /// that only changes how an already-redirected process is routed raises neither of the two
    /// events above — nothing was attached or detached — so a view showing that routing listens
    /// here. Raised on the caller's thread, outside the engine's lock.
    /// </summary>
    public event Action? ConfigurationApplied;

    /// <param name="inventory">
    /// The machine-wide process table. It is started by the host and outlives every engine run, so
    /// it is NOT owned here and never disposed by Stop.
    /// </param>
    /// <param name="outbounds">
    /// The live instance of every outbound. Like the process table it belongs to the application:
    /// a VPN outbound's instance IS its tunnel, and a tunnel must not be torn down because the user
    /// switched redirection off. Stop therefore leaves it alone, and Start only reconciles it with
    /// the configuration it was handed.
    /// </param>
    public RedirectEngine(
        IProcessRedirectorFactory redirectorFactory,
        ProcessInventory inventory,
        IHostNameInspector hostNameInspector,
        OutboundRegistry outbounds,
        ILoggerFactory loggerFactory)
    {
        _redirectorFactory = redirectorFactory ?? throw new ArgumentNullException(nameof(redirectorFactory));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _hostNameInspector = hostNameInspector ?? throw new ArgumentNullException(nameof(hostNameInspector));
        _outbounds = outbounds ?? throw new ArgumentNullException(nameof(outbounds));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<RedirectEngine>();
        // Every instance this engine routes through is dropped by its owner, and this is how the
        // things keyed by outbound hear about it. Subscribed for the life of the engine rather than
        // of a run: a VPN tunnel can die while redirection is switched off, and the UDP tunnels
        // built on it are just as dead either way.
        _outbounds.InstanceDropped += OnOutboundInstanceDropped;
    }

    public async Task StartAsync(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) throw new InvalidOperationException("Engine already running");

            _config = config;

            // Before the lock. The instances outlive the run, so this picks up whatever was edited
            // while the engine was off rather than starting from an empty cache — and leaves a VPN
            // tunnel that is already up exactly where it is. Rebuilding a stale one can mean
            // putting a tunnel down, which is not work to do inside the monitor the packet path
            // reads through.
            await ReconcileOutboundsAsync(config).ConfigureAwait(false);

            EngineRun run = BuildRun(config);
            try
            {
                lock (_stateLock)
                {
                    // Published BEFORE the driver handles open. The handlers the options name all
                    // go through _run, so a connection accepted by the relay the instant it starts
                    // finds a run that is already whole — where the old code assigned the host-name
                    // resolver and the UDP forwarder AFTER Start and left a window in which a
                    // connection would have been routed with neither.
                    _run = run;
                }

                run.Redirector.Start();

                // Only now: attaching a pid calls into the redirector, which refuses before Start.
                run.Tracker.Start(
                    config.ProcessRules,
                    attachFromProcessEvents: config.ProcessDetection == ProcessDetectionMode.ProcessEvents);
            }
            catch
            {
                // Most often the driver refusing because the process is not elevated. Without this
                // the half-started run stayed in the field: its handles stayed open, its subprocess
                // kept running, and the next press of Start built a second one beside it.
                lock (_stateLock) { _run = null; }
                run.Cancel();
                run.Tracker.ProcessAttached -= OnProcessAttached;
                run.Tracker.ProcessDetached -= OnProcessDetached;
                await run.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _logger.LogInformation(
                "engine started; relay tcp={Tcp} udp={Udp} tcpV6={TcpV6} udpV6={UdpV6}, ipv6={Ipv6Mode}",
                run.Redirector.TcpRelayPort, run.Redirector.UdpRelayPort,
                run.Redirector.TcpRelayPortV6, run.Redirector.UdpRelayPortV6, config.Ipv6);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // Builds the run's parts in the one order they can be built in: the redirector needs the
    // options, which name the handlers; everything else needs the redirector. Nothing here starts
    // pumping, so a failure leaves only ordinary objects to drop.
    private EngineRun BuildRun(AppConfig config)
    {
        var cts = new CancellationTokenSource();
        var resolvers = new ResolverSlot(BuildResolver(config, new Dictionary<uint, IReadOnlyList<Guid>>()));

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
            ShouldRedirectUdp = ShouldRedirectUdpFlow,
            // Socket-sniffing mode: the redirector listens to every process on the machine and
            // asks this about each pid it has not seen. Left null in process-event mode, where
            // the tracker names the pids instead.
            ShouldTrackProcess = config.ProcessDetection == ProcessDetectionMode.NetworkSniff
                ? ShouldRedirectProcess
                : null,
        };

        IProcessRedirector redirector = _redirectorFactory.Create(options);
        var hostNames = new ConnectionHostNameResolver(_hostNameInspector, redirector.ReverseDns);
        var udpForwarder = new UdpProxyForwarder(
            redirector, _loggerFactory.CreateLogger<UdpProxyForwarder>(), cts.Token);

        // The process table is already running and already knows every process on the machine, so
        // starting is a matter of reading it rather than of discovering anything.
        var tracker = new ProcessRuleTracker(_loggerFactory.CreateLogger<ProcessRuleTracker>(), _inventory);
        tracker.ProcessAttached += OnProcessAttached;
        tracker.ProcessDetached += OnProcessDetached;

        return new EngineRun(redirector, tracker, hostNames, udpForwarder, resolvers, cts);
    }

    // Applies an edited configuration without dropping the redirector: rules, outbounds and DNS
    // preferences take effect on the NEXT connection. Options that live in the WinDivert handles
    // (the IPv6 mode, DoH) need a restart — the UI says so rather than silently ignoring them.
    public async Task ApplyConfigAsync(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock) { _config = config; }

            EngineRun? run = _run;
            if (run is null) return;

            // Outside the state lock, and this is the point of the whole exercise: only the
            // outbounds that actually changed are rebuilt, but rebuilding one can mean putting a
            // VPN tunnel down and closing its UDP tunnels. Doing that inside the monitor the packet
            // path and the next Save both queue behind is what the user felt as the window
            // freezing on Save.
            //
            // Throwing them all away on every save is what used to kill a running VPN tunnel — and
            // make the next request re-handshake it — because the user ticked a checkbox on another
            // tab.
            await ReconcileOutboundsAsync(config).ConfigureAwait(false);

            lock (_stateLock)
            {
                run.Tracker.ApplyRules(config.ProcessRules);
                RebuildResolver();
            }

            // The connections already running are read against the new rules as well. One the new
            // configuration would route somewhere else is closed — the application reconnects, and
            // the reconnect is captured and routed afresh. One still routed the same way is left
            // alone: a download in progress does not restart over an unrelated edit.
            //
            // Also outside the lock: closing one cancels its token, and a cancellation callback
            // runs on the thread that cancels.
            int closed = _liveConnections.CloseWhereRouteChanged(run.Resolver, (live, now) =>
                _logger.LogInformation(
                    "tcp pid={Pid} {Target} is being closed: the configuration now routes it via {New} instead of {Old}",
                    live.Target.ProcessId, live.Target, now.Outbound.Name, live.OutboundName));

            // Connections that were open before their process was attached have been passing
            // through untouched, with the real address on them. Saving is the moment the user
            // asks for the configuration to hold for everything, so they are reset now; the
            // reconnects are captured from their SYN like any new connection.
            int reset = run.Redirector.ResetEscapedFlows();

            _logger.LogInformation(
                "configuration applied: {Closed} connection(s) closed for a changed route, {Reset} pre-existing flow(s) reset",
                closed, reset);
        }
        finally
        {
            _lifecycle.Release();
        }

        try { ConfigurationApplied?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "a ConfigurationApplied subscriber threw"); }
    }

    /// <summary>
    /// Redirects one specific process (and, by default, whatever it spawns) without a rule
    /// describing it. This is how you redirect "this browser I just launched" instead of every
    /// process that happens to share its file name — the user's own copy included.
    /// </summary>
    public TrackedProcess? AttachProcessId(uint processId, Guid policyId, bool includeChildren = true)
    {
        EngineRun run = _run ?? throw new InvalidOperationException("Engine is not running");

        return run.Tracker.AttachProcessId(processId, policyId, includeChildren);
    }

    /// <summary>
    /// Runs one process-discovery pass immediately instead of waiting for the next event or poll.
    /// The "launch suspended" flow needs this: the process must be adopted while it is still
    /// frozen, otherwise its first connection is out before the redirect attaches.
    /// </summary>
    public void ForceProcessScan()
    {
        // The table first, then the filters against it: a process frozen a moment ago is not in the
        // table yet, and matching a table that does not contain it would attach nothing.
        _inventory.Refresh();
        _run?.Tracker.MatchEverything();
    }

    /// <remarks>
    /// The state lock is held only long enough to take the run's parts away from the packet path.
    /// Closing them is awaited outside it, because a UDP tunnel takes up to two seconds to close
    /// and the redirector unloads a driver — none of which the next Start should be queued behind
    /// inside a monitor. <see cref="_lifecycle"/> is what keeps Start and Stop from overlapping now
    /// that the lock no longer spans the whole of either.
    /// </remarks>
    public async Task StopAsync()
    {
        EngineRun? run;

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                run = _run;
                if (run is null) return;
                _run = null;

                run.Cancel();

                // Unsubscribed here rather than with the disposal below: an event arriving after
                // the run has been taken away would find one that no longer exists. The process
                // table the tracker read stays running — it belongs to the application, not here.
                run.Tracker.ProcessAttached -= OnProcessAttached;
                run.Tracker.ProcessDetached -= OnProcessDetached;
            }

            // Outside the lock: this waits on UDP tunnels and unloads a driver. EngineRun owns the
            // order the parts go down in.
            await run.DisposeAsync().ConfigureAwait(false);

            // The outbound instances are deliberately left alone. A VPN outbound's instance IS the
            // tunnel: the user's session with their provider, which they switched on themselves and
            // which nothing here has been asked to end. Switching redirection off drops no tunnel,
            // and the proxies are only settings until a connection asks for one.

            _logger.LogInformation("engine stopped");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // Asked by the redirector's socket pump, once per process. The tracker does the deciding; this
    // only exists because the options are built before the tracker is.
    private bool? ShouldRedirectProcess(uint processId) => _run?.Tracker.ShouldRedirect(processId);

    // Rebuilds only the outbound instances the configuration has actually changed. Shared by Start
    // and ApplyConfig: an edit made while the engine was off has to reach the registry exactly as
    // one made while it runs. What follows from a drop is not handled here — see
    // OnOutboundInstanceDropped, which covers the drops nobody asked this engine for as well.
    private Task ReconcileOutboundsAsync(AppConfig config)
        => _outbounds.ReconcileAsync(config.Outbounds, config.WireProxyPath);

    // An outbound's instance has been thrown away, by an edit or by the VPN supervisor giving up on
    // a tunnel. Everything else keyed by that outbound is now pointing at something that no longer
    // exists.
    //
    // Raised on whichever thread dropped it, which is the supervision thread as often as the UI's.
    // Both of these are safe there: the capability table is concurrent, and the forwarder hands the
    // slow part of closing a tunnel to the thread pool. The forwarder field itself may be read as
    // null by a drop that lands while the engine is stopped, which is right — there are no tunnels.
    private void OnOutboundInstanceDropped(Guid outboundId)
    {
        // The edit may be exactly the fix for what we learned (a proxy that now has an IPv6 route),
        // so an outbound that will be rebuilt gets a clean slate.
        _ipv6Capability.Reset(outboundId);
        // Its UDP tunnels are pointed at an instance that no longer exists. Missing this is how a
        // VPN that dropped and came back carried TCP again while its UDP stayed dead: the
        // supervisor threw the instance away, and nothing told the forwarder.
        _run?.UdpForwarder.InvalidateOutbound(outboundId);
    }

    // ---- process scope ----------------------------------------------------------------------

    private void OnProcessAttached(TrackedProcess process)
    {
        try
        {
            _run?.Redirector.AddTrackedProcessId(process.ProcessId);
            RebuildResolver();
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
            _run?.Redirector.RemoveTrackedProcessId(process.ProcessId);
            RebuildResolver();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "detaching pid={Pid} failed", process.ProcessId);
        }
        ProcessDetached?.Invoke(process);
    }

    // Called from two directions: the UI thread applying an edited configuration, and the process
    // event thread every time a process is attached or detached.
    //
    // All of it is under the lock, and both halves have to be. Reading _config outside it let the
    // event thread build a resolver from the configuration as it was BEFORE a save and then write
    // that over the one ApplyConfig had just published: the log said "configuration applied" while
    // every new connection kept following the old policies until the next attach happened to
    // rebuild it again. Building the policy map outside it has the same shape — two attaches at
    // once, the one that finishes second overwrites with a map that is missing the other's process.
    private void RebuildResolver()
    {
        lock (_stateLock)
        {
            // A run that has just been taken away has nothing left to route, so there is no table
            // to rebuild for it. This used to publish one built from an empty policy map, which
            // nothing would ever read.
            EngineRun? run = _run;
            if (run is null) return;

            run.UseResolver(BuildResolver(_config, run.Tracker.BuildPolicyMap()));
        }
    }

    private static RoutingPolicyResolver BuildResolver(
        AppConfig config, IReadOnlyDictionary<uint, IReadOnlyList<Guid>> policyMap)
        => new RoutingPolicyResolver(config.Policies, config.Outbounds, policyMap);

    private static Uri ParseDohEndpoint(string? raw)
        => Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) ? uri : new Uri("https://1.1.1.1/dns-query");

    // ---- TCP --------------------------------------------------------------------------------

    private async Task HandleTcpAsync(RedirectedTcpConnection connection, CancellationToken ct)
    {
        // Taken once, at the top, and used for the whole connection. Stop may publish null a
        // microsecond later; this connection is then ended by its token, not by finding half a run.
        EngineRun? run = _run;
        if (run is null) return;

        string processName = run.Tracker.TryGetTracked(connection.ProcessId, out TrackedProcess? tracked)
            ? tracked!.Name
            : $"pid {connection.ProcessId}";

        var info = new ConnectionInfo(
            connection.ProcessId, processName, connection.OriginalDestination, connection.Statistics);
        Connections.Open(info);

        LiveTcpConnection? live = null;
        try
        {
            // Name first: SNI / Host header, then whatever DNS taught us about this IP.
            long nameStarted = Stopwatch.GetTimestamp();
            string? host = await run.HostNames
                .TryResolveAsync(connection, HostPeekTimeout, ct).ConfigureAwait(false);
            TimeSpan nameTime = Stopwatch.GetElapsedTime(nameStarted);
            info.Host = host;

            var target = new RouteTarget(
                connection.ProcessId,
                connection.OriginalDestination.Address,
                connection.OriginalDestination.Port,
                host);

            RouteDecision decision = run.Resolver.Resolve(target);
            info.OutboundName = decision.Outbound.Name;
            info.RouteReason = decision.Reason;
            Connections.Update(info);

            _logger.LogInformation("tcp pid={Pid} {Target} -> {Decision}", connection.ProcessId, target, decision);

            if (decision.IsBlocked)
            {
                info.Error = "blocked by rule";
                return;
            }

            // From here the connection can be closed by a configuration change: the tunnel runs
            // on the registry's token, and the registry knows what would have to change.
            live = _liveConnections.Register(target, decision.Outbound, info, closeClient: connection.ClientTcp.Close, ct);
            await TunnelAsync(connection, decision.Outbound, host, info, nameTime, live.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine stopping, or the connection was cancelled — nothing to report, unless it was
            // the configuration that ended it: that one the user asked for and may look for.
            if (live?.ClosedForChangedRoute == true)
                _logger.LogInformation("tcp pid={Pid} -> {Destination} {Reason}", connection.ProcessId, connection.OriginalDestination, info.Error);
        }
        catch (Exception) when (live?.ClosedForChangedRoute == true)
        {
            // The socket was closed under the copy loop; whatever it threw is that closure.
            _logger.LogInformation("tcp pid={Pid} -> {Destination} {Reason}", connection.ProcessId, connection.OriginalDestination, info.Error);
        }
        catch (Exception ex)
        {
            info.Error = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogWarning(ex, "tcp pid={Pid} -> {Destination} failed", connection.ProcessId, connection.OriginalDestination);
        }
        finally
        {
            if (live != null) _liveConnections.Unregister(live);
            Connections.Close(info);
        }
    }

    private async Task TunnelAsync(
        RedirectedTcpConnection connection, Outbound outbound, string? host, ConnectionInfo info,
        TimeSpan nameTime, CancellationToken ct)
    {
        IPEndPoint destination = connection.OriginalDestination;
        bool isIpv6 = destination.Address.AddressFamily == AddressFamily.InterNetworkV6;

        // Through a proxy, hand over the HOST NAME when we have one: the proxy then resolves it on
        // its own side (remote DNS), so the destination is never leaked to the local resolver and
        // CDN answers stay correct for the proxy's location. That is also the IPv6 fallback that
        // costs nothing — a name lets an outbound without an IPv6 route pick the A record itself.
        // Going direct, use the IP the process itself chose: re-resolving could pick a different
        // server than the one the application decided on.
        bool byName = !outbound.IsDirect && !string.IsNullOrEmpty(host);

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

        IProxySource source = _outbounds.GetOrCreate(outbound).Source;
        IConnectSource? tunnel = null;
        Guid tunnelId = Guid.NewGuid();
        try
        {
            tunnel = await source.GetConnectSourceAsync(tunnelId, ct).ConfigureAwait(false);

            string targetHost = byName ? host! : destination.Address.ToString();
            // UriBuilder brackets an IPv6 literal for us ("tcp://[2606:4700::1111]:443"), which is
            // what the SOCKS5/HTTP address parsers expect to see.
            var targetUri = new UriBuilder("tcp", targetHost, destination.Port).Uri;

            long connectStarted = Stopwatch.GetTimestamp();
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

            // The three legs a connection waits on, so a slow site can be blamed on the right one:
            // the handshake through the relay (a stalled pump), the wait for the client to name
            // its host (a preconnect that says nothing), or the upstream connect (the network).
            _logger.LogInformation(
                "tcp pid={Pid} -> {Target} up via {Outbound}: handshake {HandshakeMs}ms, name {NameMs}ms, connect {ConnectMs}ms",
                connection.ProcessId, targetUri.Authority, outbound.Name,
                (long)connection.CaptureToAccept.TotalMilliseconds,
                (long)nameTime.TotalMilliseconds,
                (long)Stopwatch.GetElapsedTime(connectStarted).TotalMilliseconds);

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
        if (outbound.IsDirect) return;
        if (!_ipv6Capability.RecordIpv6Failure(outbound)) return;

        _outbounds.SetIpv6Support(outbound.Id, false);
        _logger.LogInformation(ex,
            "outbound {Outbound} marked IPv4-only after {Destination} failed. Later IPv6 destinations go "
            + "out over IPv4, by name where one is known; set Ipv6Support=Enabled to override",
            outbound.Name, destination);
    }

    // ---- UDP --------------------------------------------------------------------------------

    // Asked on the packet path, before a UDP flow is redirected at all.
    //
    // A datagram routed Direct must never reach the relay: the relay forwards from its own socket,
    // on a port nothing can map back to the process, so the query leaves and the answer is lost.
    // That is what broke DNS — a browser with its own resolver got no answers at all. Leaving the
    // flow untouched is the only thing that actually delivers "direct": the datagram goes out of
    // the process's own socket and the reply comes straight back to it.
    //
    // The cost is stated plainly: a passed-through flow carries the machine's real address, which
    // for DNS is the same exposure SystemSniff already accepts by definition. A user who wants
    // their DNS tunnelled says so with a rule — a Protocol "udp" rule, or a Port "53" one — and
    // the flow then resolves to an outbound instead of Direct and comes back here as "redirect".
    //
    // Block still goes through the relay: it is claimed by NAT and dropped there, so nothing about
    // it reaches the wire. Passing a blocked datagram would leak the very thing it must not.
    private bool ShouldRedirectUdpFlow(uint processId, IPAddress destination, ushort destinationPort, bool isIpv6)
    {
        EngineRun? run = _run;
        // Nothing left to redirect it to. Leaving the flow alone is also the safe answer: the
        // datagram goes out of the process's own socket, exactly as it would with the engine off.
        if (run is null) return false;

        string? host = run.Redirector.ReverseDns.Resolve(destination);
        var target = new RouteTarget(processId, destination, destinationPort, host, isUdp: true);

        RouteDecision decision = run.Resolver.ResolveUdp(target);
        if (!decision.IsDirect) return true;

        _logger.LogDebug("udp pid={Pid} -> {Target} left direct, unredirected ({Reason})",
            processId, target, decision.Reason);
        return false;
    }

    // Returning the payload lets the relay send it out directly; returning null means "handled or
    // dropped — do not send". Anything that cannot be tunnelled is dropped rather than leaked.
    private byte[]? HandleUdpDatagram(RedirectedUdpDatagram datagram, CancellationToken ct)
    {
        EngineRun? run = _run;
        // Dropped rather than passed on: the run that would have tunnelled it is gone, and sending
        // it out from here would carry the machine's real address.
        if (run is null) return null;

        try
        {
            string? host = run.Redirector.ReverseDns.Resolve(datagram.OriginalDestination.Address);
            var target = new RouteTarget(
                datagram.ProcessId,
                datagram.OriginalDestination.Address,
                datagram.OriginalDestination.Port,
                host,
                isUdp: true);

            RouteDecision decision = run.Resolver.ResolveUdp(target);

            if (decision.IsBlocked) return null;

            if (decision.IsDirect)
            {
                // Normally unreachable: ShouldRedirectUdpFlow keeps a Direct flow away from the
                // relay entirely. It is still reached when the answer changed between the packet
                // path and here — a DNS answer landing in between gives the flow a name it did not
                // have, and a rule that then claims it. Forwarding from the relay's socket is all
                // that is left at this point, and its reply has nowhere to go, so the sender sees
                // one lost datagram and retries.
                _logger.LogDebug(
                    "udp pid={Pid} -> {Destination} resolved Direct after it was already redirected; "
                    + "forwarding without a reply path, the sender will retry",
                    datagram.ProcessId, datagram.OriginalDestination);
                return datagram.Payload;
            }

            bool isIpv6 = datagram.OriginalDestination.AddressFamily == AddressFamily.InterNetworkV6;
            // A UDP datagram carries no name to fall back on, so an outbound without an IPv6 route
            // has nothing to send it over. Dropping is the safe answer: letting it out direct would
            // expose the real address.
            if (isIpv6 && !_ipv6Capability.AllowsIpv6(decision.Outbound))
            {
                _logger.LogDebug(
                    "udp pid={Pid} -> {Destination} dropped: {Outbound} has no IPv6 route",
                    datagram.ProcessId, datagram.OriginalDestination, decision.Outbound.Name);
                return null;
            }

            IProxySource source = _outbounds.GetOrCreate(decision.Outbound).Source;
            bool queued = run.UdpForwarder.Send(
                decision.Outbound.Id, source,
                (ushort)datagram.OriginalSource.Port,
                datagram.OriginalDestination,
                datagram.Payload,
                isIpv6);
            if (!queued)
                _logger.LogDebug("udp pid={Pid} -> {Destination} dropped, the tunnel is not ready yet", datagram.ProcessId, datagram.OriginalDestination);
            return null;
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
        if (outbound.IsBlocked) return "Block never connects anywhere.";

        // A VPN test starts its own wireproxy subprocess so it never disturbs a tunnel live traffic
        // is already using — and it has to put that subprocess down itself. The factory owns
        // nothing it builds, so what comes back is ours alone and its disposal is ours too.
        OutboundSourceFactory factory = OutboundSourceFactory.CreateDefault();
        IOutboundInstance? instance = null;
        IConnectSource? tunnel = null;
        try
        {
            instance = factory.Create(outbound, loggerFactory, wireProxyPath);
            tunnel = await instance.Source.GetConnectSourceAsync(Guid.NewGuid(), ct).ConfigureAwait(false);
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
            // After the tunnel: for a VPN this is what kills wireproxy, and without it every press
            // of Test left one more subprocess holding a SOCKS port and a WireGuard session for as
            // long as the app ran.
            if (instance is not null)
            {
                try { await instance.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outbounds.InstanceDropped -= OnOutboundInstanceDropped;
        await StopAsync().ConfigureAwait(false);
    }

    // What the container calls: ServiceProvider.Dispose refuses a singleton that offers
    // DisposeAsync alone. The application itself goes through DisposeAsync.
    public void Dispose()
    {
        // The registry outlives this engine, so leaving the subscription behind would keep a
        // disposed engine reachable and being told about drops it can do nothing with.
        _outbounds.InstanceDropped -= OnOutboundInstanceDropped;
        StopAsync().GetAwaiter().GetResult();
    }
}
