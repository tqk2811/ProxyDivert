using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;

namespace ProxyDivert.Core.Outbounds;

// Builds the instance of an outbound, and keeps ONE per outbound id.
//
// Sharing matters: an IProxySource is a factory of tunnels, not a connection, and some
// implementations keep state (SSH/WireGuard sessions) that must not be rebuilt per connection.
// Invalidate(id) drops the cached instance when the user edits or deletes an outbound, so the next
// connection picks up the new settings.
//
// What each kind actually builds is not here: one IOutboundSourceBuilder per OutboundKind answers
// that, including Block, whose answer is "nothing - close the connection instead".
public sealed class OutboundSourceFactory : IDisposable, IAsyncDisposable
{
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Dictionary<OutboundKind, IOutboundSourceBuilder> _builders;
    private readonly ConcurrentDictionary<Guid, CachedInstance> _cache = new ConcurrentDictionary<Guid, CachedInstance>();

    /// <summary>
    /// Where wireproxy.exe lives, for VPN outbounds. Null or empty means "look next to this
    /// executable, then on PATH" — which is what a user who dropped the binary in the tool's folder
    /// expects. One setting for the machine rather than one per outbound: it is the same binary
    /// whichever tunnel it runs.
    /// </summary>
    /// <remarks>
    /// Set through <see cref="ApplyOutboundsAsync"/> rather than directly, because changing it makes
    /// every live VPN instance stale and that has to be noticed in the same step.
    /// </remarks>
    public string? WireProxyPath { get; private set; }

    public OutboundSourceFactory(ILoggerFactory? loggerFactory = null, string? wireProxyPath = null)
        : this(DefaultBuilders(), loggerFactory, wireProxyPath)
    {
    }

    public OutboundSourceFactory(
        IEnumerable<IOutboundSourceBuilder> builders,
        ILoggerFactory? loggerFactory = null,
        string? wireProxyPath = null)
    {
        if (builders is null) throw new ArgumentNullException(nameof(builders));

        _builders = new Dictionary<OutboundKind, IOutboundSourceBuilder>();
        foreach (IOutboundSourceBuilder builder in builders)
        {
            // Two builders claiming one kind is a wiring mistake, and the one that would win is
            // whichever the container happened to enumerate first — so say so here instead.
            if (_builders.TryGetValue(builder.Kind, out IOutboundSourceBuilder? existing))
            {
                throw new InvalidOperationException(
                    $"Two builders claim {builder.Kind}: {existing.GetType().Name} and {builder.GetType().Name}.");
            }
            _builders.Add(builder.Kind, builder);
        }

        _loggerFactory = loggerFactory;
        WireProxyPath = wireProxyPath;
    }

    /// <summary>Every kind the application knows how to build, for a caller outside the container.</summary>
    public static IEnumerable<IOutboundSourceBuilder> DefaultBuilders()
    {
        yield return new DirectOutboundBuilder();
        yield return new BlockOutboundBuilder();
        yield return new HttpProxyOutboundBuilder();
        yield return new Socks4OutboundBuilder();
        yield return new Socks5OutboundBuilder();
        yield return new VpnOutboundBuilder();
    }

    public IProxySource GetOrCreate(Outbound outbound) => GetOrCreateInstance(outbound).Source;

    /// <summary>
    /// The live instance of an outbound, building it if this is the first time anything has needed
    /// it. Callers that only want somewhere to send a connection take <see cref="GetOrCreate"/>;
    /// the supervisor wants the whole instance, because for a VPN the instance is the tunnel.
    /// </summary>
    public IOutboundInstance GetOrCreateInstance(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        return _cache.GetOrAdd(
            outbound.Id,
            _ => new CachedInstance(OutboundSignature.Of(outbound, WireProxyPath), () => Create(outbound))).Instance;
    }

    /// <summary>
    /// The live instance of an outbound, or null when nothing has needed it yet. Does not build
    /// one — a caller that wants it built asks <see cref="GetOrCreate"/>.
    /// </summary>
    public IProxySource? Find(Guid outboundId) => FindInstance(outboundId)?.Source;

    public IOutboundInstance? FindInstance(Guid outboundId)
        => _cache.TryGetValue(outboundId, out CachedInstance? cached) ? cached.InstanceIfBuilt : null;

    /// <summary>
    /// Reconciles the cache with an edited configuration, disposing only the instances that are
    /// now wrong, and returns the ids that were dropped.
    /// </summary>
    /// <remarks>
    /// The alternative — <see cref="InvalidateAllAsync"/> on every save — is what made saving an
    /// unrelated setting tear down a running VPN tunnel and re-handshake it. Everything keyed by
    /// outbound elsewhere (learned IPv6 capability, UDP tunnels) is invalidated from the returned
    /// set, so those stay in step without also being thrown away wholesale.
    /// </remarks>
    public async Task<IReadOnlyCollection<Guid>> ApplyOutboundsAsync(
        IEnumerable<Outbound> outbounds, string? wireProxyPath)
    {
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));

        WireProxyPath = wireProxyPath;

        var current = new Dictionary<Guid, Outbound>();
        foreach (Outbound outbound in outbounds) current[outbound.Id] = outbound;

        var invalidated = new List<Guid>();
        foreach (var kv in _cache)
        {
            // An outbound that has disappeared from the configuration cannot be routed to any
            // more, so its instance is only holding a subprocess or a socket open.
            bool stale = !current.TryGetValue(kv.Key, out Outbound? outbound)
                || !string.Equals(kv.Value.Signature, OutboundSignature.Of(outbound, wireProxyPath), StringComparison.Ordinal);
            if (!stale) continue;

            await InvalidateAsync(kv.Key).ConfigureAwait(false);
            invalidated.Add(kv.Key);
        }
        return invalidated;
    }

    /// <summary>
    /// Turns IPv6 off (or back on) for the live instance of an outbound. Used when a connection
    /// teaches us that an Ipv6Support.Auto outbound has no IPv6 route: the source then stops
    /// handing IPv6 addresses to it on its own. No-op for an outbound whose answer cannot change
    /// once it has been built.
    /// </summary>
    public void SetIpv6Support(Guid outboundId, bool supported)
    {
        // FindInstance, not GetOrCreateInstance: this is what a failed connection teaches us, so
        // the instance always exists by now — and building one here just to configure it would be
        // absurd.
        FindInstance(outboundId)?.SetIpv6Support(supported);
    }

    // Call after the user edits or removes an outbound.
    public async ValueTask InvalidateAsync(Guid outboundId)
    {
        if (_cache.TryRemove(outboundId, out CachedInstance? cached))
            await DisposeInstanceAsync(cached.InstanceIfBuilt).ConfigureAwait(false);
    }

    public async ValueTask InvalidateAllAsync()
    {
        foreach (var kv in _cache) await DisposeInstanceAsync(kv.Value.InstanceIfBuilt).ConfigureAwait(false);
        _cache.Clear();
    }

    // Builds an instance without caching it — used by the UI's "test this outbound" button, so a
    // test never disturbs the instance live traffic is using. What comes back belongs to the caller.
    public IOutboundInstance Create(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));

        var context = new OutboundBuildContext
        {
            Signature = OutboundSignature.Of(outbound, WireProxyPath),
            LoggerFactory = _loggerFactory,
            WireProxyPath = WireProxyPath,
        };

        IOutboundInstance instance = BuilderFor(outbound).Build(outbound, context);
        // Ipv6Support.Disabled is a statement about this way out, so it belongs on the instance
        // itself: LocalProxySource then filters name lookups down to A records instead of handing
        // the connection an AAAA it cannot use.
        if (outbound.Ipv6Support == Ipv6Support.Disabled) instance.SetIpv6Support(false);
        return instance;
    }

    private IOutboundSourceBuilder BuilderFor(Outbound outbound)
        => _builders.TryGetValue(outbound.Kind, out IOutboundSourceBuilder? builder)
            ? builder
            : throw new ArgumentOutOfRangeException(nameof(outbound), outbound.Kind, "Unknown outbound kind");

    /// <summary>
    /// Releases an instance, whatever it happens to be underneath. Public because
    /// <see cref="Create"/> hands ownership to its caller, and a wireproxy-backed instance that
    /// nobody releases leaves a subprocess running for the life of the app.
    /// </summary>
    public static async ValueTask DisposeInstanceAsync(IOutboundInstance? instance)
    {
        if (instance is null) return;
        try { await instance.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync() => await InvalidateAllAsync().ConfigureAwait(false);

    // What the container calls: ServiceProvider.Dispose refuses a singleton that offers DisposeAsync
    // alone, and this is registered as one. Blocking here cannot deadlock — every await underneath
    // is ConfigureAwait(false) — but it does hold the calling thread for as long as putting the
    // tunnels down takes, so the application goes through DisposeAsync instead.
    public void Dispose() => InvalidateAllAsync().AsTask().GetAwaiter().GetResult();

    // The instance plus what it was built from, so a later configuration can be compared against
    // it without building anything.
    //
    // Built lazily so that constructing this is free. ConcurrentDictionary.GetOrAdd may run its
    // factory on several threads at once and keep only one result; when the factory is what spawns
    // wireproxy or dials a tunnel, the copies it discards are not merely wasted work — nobody holds
    // them, so nobody ever shuts them down. Making the expensive part a Lazy means only the entry
    // the dictionary actually kept is ever built.
    private sealed class CachedInstance
    {
        private readonly Lazy<IOutboundInstance> _instance;

        public CachedInstance(string signature, Func<IOutboundInstance> build)
        {
            Signature = signature;
            _instance = new Lazy<IOutboundInstance>(build, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// What this entry will be built from. The instance carries the same stamp, but an entry
        /// nothing has needed yet has no instance to ask.
        /// </summary>
        public string Signature { get; }

        /// <summary>The instance, built on first use.</summary>
        public IOutboundInstance Instance => _instance.Value;

        /// <summary>
        /// The instance if something has already asked for it, else null — for callers that must
        /// not cause one to be built.
        /// </summary>
        public IOutboundInstance? InstanceIfBuilt => _instance.IsValueCreated ? _instance.Value : null;
    }
}
