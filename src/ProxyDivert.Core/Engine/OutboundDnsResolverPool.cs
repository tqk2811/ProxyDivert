using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.SecureDns;
using TqkLibrary.WinDivert.SecureDns.Interfaces;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// The DNS over HTTPS resolvers of one engine run, one per outbound the HTTPS goes out through, and
/// per timeout kind.
/// </summary>
/// <remarks>
/// <para>
/// Owns every resolver it hands out: the secure DNS decider names them to the library, which only
/// borrows them. They are created on first use — on the capture pump thread, which is fine because
/// creating one does no I/O — and the HTTPS connection behind each is opened on its first query.
/// </para>
/// <para>
/// Two timeouts, because the policy decides what a failure means. A policy that falls back to plain
/// DNS has to fail before the Windows stub resolver gives up on the query (about a second or two),
/// or the fallback reaches nobody; one that does not can afford to wait for a slow tunnel.
/// </para>
/// <para>
/// A resolver that has to go — its outbound's instance was dropped, or the whole pool is being
/// replaced — is retired rather than disposed on the spot: queries may be in flight on it, and
/// disposing would fail every one of them. It is disposed once the longest timeout has passed.
/// Stopping the engine disposes at once; the redirector is gone by then, so nothing is in flight.
/// </para>
/// </remarks>
internal sealed class OutboundDnsResolverPool : IDisposable
{
    /// <summary>For a policy that falls back to plain DNS: well under the stub resolver's patience.</summary>
    public static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(2);

    /// <summary>For a policy that does not fall back: a tunnel may be slow to dial.</summary>
    public static readonly TimeSpan NormalTimeout = TimeSpan.FromSeconds(5);

    // How long an idle HTTPS connection through an outbound is kept before it is replaced, so a
    // tunnel that changed underneath does not hold a dead connection for ever.
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<(Guid OutboundId, bool Short), Lazy<IDnsResolver>> _resolvers = new();
    private readonly Func<Outbound, TimeSpan, IDnsResolver> _create;
    private readonly TimeSpan _retireDelay;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<List<Lazy<IDnsResolver>>> _retiredLists = new();
    private int _disposed;

    /// <param name="endpoint">The DoH server every resolver of this pool asks.</param>
    /// <param name="outbounds">Where the resolvers of a non-direct outbound dial their HTTPS through. Not owned.</param>
    public OutboundDnsResolverPool(Uri endpoint, OutboundRegistry outbounds, ILoggerFactory loggerFactory)
    {
        if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));
        if (loggerFactory is null) throw new ArgumentNullException(nameof(loggerFactory));

        Endpoint = endpoint;
        _logger = loggerFactory.CreateLogger<OutboundDnsResolverPool>();
        // A query on a retiring resolver may still wait queued, then for a VPN to be ready, then for
        // TLS, all before its HttpClient timeout (NormalTimeout) starts counting down to failure.
        _retireDelay = NormalTimeout + TimeSpan.FromSeconds(2);
        ILogger<DohResolver> resolverLogger = loggerFactory.CreateLogger<DohResolver>();
        _create = (outbound, timeout) => new DohResolver(
            resolverLogger, CreateHandler(outbound, outbounds), disposeHandler: true, endpoint, timeout);
    }

    /// <summary>For tests: what a resolver is, and how long a retired one lives on.</summary>
    internal OutboundDnsResolverPool(
        Uri endpoint, Func<Outbound, TimeSpan, IDnsResolver> create, TimeSpan retireDelay, ILogger logger)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _retireDelay = retireDelay;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Uri Endpoint { get; }

    /// <summary>
    /// The resolver for queries going out through <paramref name="outbound"/>, created on first use.
    /// Throws <see cref="ObjectDisposedException"/> once the pool is disposed.
    /// </summary>
    public IDnsResolver Get(Outbound outbound, bool shortTimeout)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        ThrowIfDisposed();

        Lazy<IDnsResolver> lazy = _resolvers.GetOrAdd(
            (outbound.Id, shortTimeout),
            _ => new Lazy<IDnsResolver>(
                () => _create(outbound, shortTimeout ? ShortTimeout : NormalTimeout),
                LazyThreadSafetyMode.ExecutionAndPublication));
        IDnsResolver resolver = lazy.Value;

        // Lost a race with Dispose: what was just created would never be disposed by anyone else.
        if (Volatile.Read(ref _disposed) != 0)
        {
            DisposeAll();
            ThrowIfDisposed();
        }
        return resolver;
    }

    /// <summary>
    /// Forgets the resolvers of one outbound, whose instance has been dropped, and disposes them
    /// once the queries in flight on them have had their time.
    /// </summary>
    public void Invalidate(Guid outboundId)
    {
        var retired = new List<Lazy<IDnsResolver>>(2);
        foreach (bool kind in new[] { false, true })
        {
            if (_resolvers.TryRemove((outboundId, kind), out Lazy<IDnsResolver>? lazy))
                retired.Add(lazy);
        }
        if (retired.Count > 0) Retire(retired);
    }

    /// <summary>
    /// Disposes every resolver once the queries in flight have had their time — for a pool being
    /// replaced by one with another endpoint while the run goes on.
    /// </summary>
    public void Retire()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Retire(TakeAll());
    }

    /// <summary>Disposes every resolver now. For the end of a run.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        DisposeAll();

        // Resolvers already retired go now too, and their delayed disposal is cancelled.
        _disposeCts.Cancel();
        List<Lazy<IDnsResolver>>[] pending;
        lock (_retiredLists)
        {
            pending = _retiredLists.ToArray();
        }
        foreach (List<Lazy<IDnsResolver>> list in pending) DisposeList(list);
    }

    private void DisposeAll()
    {
        foreach (Lazy<IDnsResolver> lazy in TakeAll()) DisposeOne(lazy);
    }

    private List<Lazy<IDnsResolver>> TakeAll()
    {
        var taken = new List<Lazy<IDnsResolver>>();
        foreach (var key in _resolvers.Keys)
        {
            if (_resolvers.TryRemove(key, out Lazy<IDnsResolver>? lazy)) taken.Add(lazy);
        }
        return taken;
    }

    private void Retire(List<Lazy<IDnsResolver>> retired)
    {
        if (_retireDelay <= TimeSpan.Zero)
        {
            foreach (Lazy<IDnsResolver> lazy in retired) DisposeOne(lazy);
            return;
        }

        lock (_retiredLists) _retiredLists.Add(retired);
        _ = Task.Delay(_retireDelay, _disposeCts.Token).ContinueWith(
            _ => DisposeList(retired),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Idempotent: the list leaves the pending set first, so whichever of the delayed task and
    // Dispose gets here first disposes it and the other finds nothing.
    private void DisposeList(List<Lazy<IDnsResolver>> list)
    {
        lock (_retiredLists)
        {
            if (!_retiredLists.Remove(list)) return;
        }
        foreach (Lazy<IDnsResolver> lazy in list) DisposeOne(lazy);
    }

    private void DisposeOne(Lazy<IDnsResolver> lazy)
    {
        if (!lazy.IsValueCreated) return;
        try { lazy.Value.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "disposing a DoH resolver threw"); }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(OutboundDnsResolverPool));
    }

    // Direct goes out of the machine's own stack. Anything else dials the DoH server's TCP
    // connection through the outbound, waiting for a supervised tunnel the way a redirected
    // connection does.
    private static HttpMessageHandler CreateHandler(Outbound outbound, OutboundRegistry outbounds)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = PooledConnectionLifetime };
        if (outbound.IsDirect) return handler;

        handler.ConnectCallback = async (context, ct) =>
        {
            IOutboundInstance instance = await outbounds.GetReadyAsync(outbound, onWaiting: null, ct).ConfigureAwait(false);
            return await ConnectSourceStream.OpenAsync(
                instance.Source, context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
        };
        return handler;
    }
}
