using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.SecureDns.Interfaces;
using TqkLibrary.WinDivert.SecureDns.Models;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// Answers the redirector's question for every DNS query on the machine: leave it alone, or resolve
/// it over DNS over HTTPS through which outbound.
/// </summary>
/// <remarks>
/// Runs on the capture pump thread, once per query, so it only reads: the routing table decides,
/// the resolver pool hands out an object that does no I/O until it is asked. Anything that throws
/// turns into "pass" — the query goes out as it would with the engine off, which is better than a
/// pump that stops. The exception is a query the routing table did claim but whose resolver could
/// not be had: if the policy forbids plain DNS it is answered with a failure, never passed —
/// passing would send the very query the policy meant to protect out in the clear.
/// </remarks>
internal sealed class SecureDnsQueryDecider
{
    private readonly SecureDnsRouteFunc _route;
    private readonly Func<DnsRouteDecision, IDnsResolver?> _resolverFor;
    private readonly ILogger _logger;

    /// <param name="route">The routing table's verdict for (pid, name, isIpv6); null is "pass".</param>
    /// <param name="resolverFor">
    /// The resolver for an outbound, and whether it needs the short timeout; null when the engine is
    /// not running (the query passes). It may throw <see cref="ObjectDisposedException"/> when the
    /// pool it read has just been retired; it is then asked once more.
    /// </param>
    public SecureDnsQueryDecider(
        Func<uint?, string, bool, DnsRouteDecision?> route,
        Func<Outbound, bool, IDnsResolver?> resolverFor,
        ILogger logger)
        : this(Wrap(route), resolverFor, logger)
    {
    }

    /// <summary>As above, for a routing table that also says why it let a query through (for the log).</summary>
    public SecureDnsQueryDecider(
        SecureDnsRouteFunc routeWithReason,
        Func<Outbound, bool, IDnsResolver?> resolverFor,
        ILogger logger)
    {
        _route = routeWithReason ?? throw new ArgumentNullException(nameof(routeWithReason));
        if (resolverFor is null) throw new ArgumentNullException(nameof(resolverFor));
        _resolverFor = d => resolverFor(d.Outbound, d.FallbackToPlainDns);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>As above, with a resolver chosen from the whole decision — outbound, timeout kind and the policy's own DoH server.</summary>
    public SecureDnsQueryDecider(
        SecureDnsRouteFunc routeWithReason,
        Func<DnsRouteDecision, IDnsResolver?> resolverFor,
        ILogger logger)
    {
        _route = routeWithReason ?? throw new ArgumentNullException(nameof(routeWithReason));
        _resolverFor = resolverFor ?? throw new ArgumentNullException(nameof(resolverFor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static SecureDnsRouteFunc Wrap(Func<uint?, string, bool, DnsRouteDecision?> route)
    {
        if (route is null) throw new ArgumentNullException(nameof(route));
        return (uint? pid, string name, bool isIpv6, out DnsPassReason reason) =>
        {
            DnsRouteDecision? decision = route(pid, name, isIpv6);
            reason = decision is null ? DnsPassReason.NoMatchingPolicy : DnsPassReason.None;
            return decision;
        };
    }

    public DnsQueryDecision Decide(in DnsQueryInfo query)
    {
        DnsRouteDecision? decision;
        DnsPassReason reason;
        try
        {
            decision = _route(query.ProcessId, query.QueryName, query.IsIpv6, out reason);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "dns pid={Pid} {Name}: pass ({Reason})", query.ProcessId, query.QueryName, DnsPassReason.RoutingError);
            return DnsQueryDecision.Pass;
        }
        if (decision is null)
        {
            // Per query on the pump thread: a Pass is the common case, so nothing is built unless asked.
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("dns pid={Pid} {Name} type={Type} -> pass ({Reason})",
                    query.ProcessId, query.QueryName, query.QueryType, reason);
            return DnsQueryDecision.Pass;
        }

        try
        {
            // The fallback flag picks the short timeout too: a fallback that arrives after the stub
            // resolver has given up is no fallback at all.
            IDnsResolver? resolver = AcquireResolver(decision);
            if (resolver is null)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("dns pid={Pid} {Name} type={Type} -> pass (engine not running)",
                        query.ProcessId, query.QueryName, query.QueryType);
                return DnsQueryDecision.Pass;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "dns pid={Pid} {Name} type={Type} -> DoH via {Outbound}, policy {Policy}, rule {Rule}, side={Side}, fallback={Fallback}",
                    query.ProcessId, query.QueryName, query.QueryType, decision.Outbound.Name, decision.Policy.Name,
                    decision.MatchedRule is null ? "(none)" : decision.MatchedRule.Matcher + ":" + decision.MatchedRule.Pattern,
                    decision.Side, decision.FallbackToPlainDns);
            }
            return DnsQueryDecision.Resolve(resolver, decision.FallbackToPlainDns);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "dns pid={Pid} {Name}: no resolver, fallback={Fallback}",
                query.ProcessId, query.QueryName, decision.FallbackToPlainDns);
            return decision.FallbackToPlainDns
                ? DnsQueryDecision.Pass
                : DnsQueryDecision.Resolve(FailingResolver.Instance, false);
        }
    }

    // A pool retired between the engine reading it and the Get lands here as ObjectDisposedException:
    // the engine now holds the replacement, so one more ask finds it (or finds no run at all).
    private IDnsResolver? AcquireResolver(DnsRouteDecision decision)
    {
        try
        {
            return _resolverFor(decision);
        }
        catch (ObjectDisposedException)
        {
            return _resolverFor(decision);
        }
    }

    // What a query is handed when it must not pass and nothing can answer it: the library gets a null
    // answer and replies SERVFAIL to the client.
    private sealed class FailingResolver : IDnsResolver
    {
        public static readonly FailingResolver Instance = new FailingResolver();

        private FailingResolver()
        {
        }

        public Uri Endpoint { get; } = new Uri("https://secure-dns.invalid/");

        public Task<byte[]?> ResolveAsync(byte[] dnsWireQuery, CancellationToken ct) => Task.FromResult<byte[]?>(null);

        public void Dispose()
        {
        }
    }
}
