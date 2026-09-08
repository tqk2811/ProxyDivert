using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Outbounds;

/// <summary>
/// The live instance of every outbound, and the only thing that disposes one.
/// </summary>
/// <remarks>
/// Sharing matters: an IProxySource is a factory of tunnels rather than a connection, and some
/// implementations keep state — an SSH session, a WireGuard handshake, a wireproxy subprocess —
/// that must not be rebuilt per connection. So there is one instance per outbound id, built the
/// first time something needs it.
///
/// Ownership is the point of this class. The instance cache used to live inside the factory, and
/// three different objects could drop an entry out from under each other: the factory reconciled
/// the cache on every save, the engine invalidated an outbound it had learned something about, and
/// the VPN supervisor invalidated one whose tunnel had died. All three called the same
/// Invalidate(id), so nothing in the code said which of them was allowed to. Here there are exactly
/// two ways an instance goes away, and they read differently on purpose:
/// <see cref="ReconcileAsync"/> is the configuration changing, and <see cref="DiscardAsync"/> is
/// the supervisor of one outbound saying the instance it watches is finished.
///
/// Either way <see cref="InstanceDropped"/> is raised, which is how everything else keyed by
/// outbound — learned IPv6 capability, open UDP tunnels — stays in step. It is raised on whichever
/// thread dropped the instance, so a handler must not block.
/// </remarks>
public sealed class OutboundRegistry : IDisposable, IAsyncDisposable
{
    private readonly OutboundSourceFactory _factory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Entry> _instances = new ConcurrentDictionary<Guid, Entry>();

    // Read on the build path from threads that never took a lock; written whenever a configuration
    // is applied. A stale read costs one instance built from the previous path, which the signature
    // comparison then rebuilds.
    private volatile string? _wireProxyPath;

    public OutboundRegistry(OutboundSourceFactory factory, ILoggerFactory? loggerFactory = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<OutboundRegistry>() ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>
    /// Where wireproxy.exe lives, for VPN outbounds. Null or empty means "look next to this
    /// executable, then on PATH" — which is what a user who dropped the binary in the tool's folder
    /// expects. One setting for the machine rather than one per outbound: it is the same binary
    /// whichever tunnel it runs.
    /// </summary>
    public string? WireProxyPath => _wireProxyPath;

    /// <summary>Raised after an instance has been disposed, with the outbound it belonged to.</summary>
    public event Action<Guid>? InstanceDropped;

    /// <summary>
    /// Tells the registry where wireproxy is, without touching anything already built.
    /// </summary>
    /// <remarks>
    /// It is here rather than only on <see cref="ReconcileAsync"/> because the VPN supervisor dials
    /// tunnels at startup, before anything has applied a configuration to the engine: the path used
    /// to reach the registry only through a reconcile, so the first tunnel of the session was built
    /// as if the setting were empty and only came right once the user pressed Start.
    ///
    /// Setting it does NOT drop the instances built from the previous path — the path is part of a
    /// VPN's signature, so a reconcile drops them, and the supervisor compares signatures for the
    /// tunnels it holds.
    /// </remarks>
    public void UseWireProxyPath(string? wireProxyPath) => _wireProxyPath = wireProxyPath;

    /// <summary>
    /// What this outbound would be built from right now, as one comparable string. Everything that
    /// has to decide "is what I am holding still the right one" asks here, so there is one answer
    /// rather than one per caller.
    /// </summary>
    public string SignatureOf(Outbound outbound) => OutboundSignature.Of(outbound, _wireProxyPath);

    /// <summary>
    /// Whether this outbound is one a supervisor can hold open — a way out that keeps a subprocess
    /// or a session between requests rather than dialling one per connection. Answered without
    /// building anything.
    /// </summary>
    public bool CanBeKeptConnected(Outbound outbound) => _factory.BuildsManagedSource(outbound);

    /// <summary>
    /// The live instance of an outbound, built if this is the first time anything has needed it.
    /// </summary>
    public IOutboundInstance GetOrCreate(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        return _instances.GetOrAdd(
            outbound.Id,
            _ => new Entry(SignatureOf(outbound), () => Build(outbound))).Instance;
    }

    /// <summary>
    /// The live instance of an outbound, or null when nothing has needed it yet. Does not build
    /// one — a caller that wants it built asks <see cref="GetOrCreate"/>.
    /// </summary>
    public IOutboundInstance? Find(Guid outboundId)
        => _instances.TryGetValue(outboundId, out Entry? entry) ? entry.InstanceIfBuilt : null;

    /// <summary>
    /// Brings the instances in line with an edited configuration, disposing only the ones that are
    /// now wrong, and returns the ids that were dropped.
    /// </summary>
    /// <remarks>
    /// The alternative — throwing every instance away on every save — is what made saving an
    /// unrelated setting tear down a running VPN tunnel and re-handshake it.
    /// </remarks>
    public async Task<IReadOnlyCollection<Guid>> ReconcileAsync(
        IEnumerable<Outbound> outbounds, string? wireProxyPath)
    {
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));

        UseWireProxyPath(wireProxyPath);

        var current = new Dictionary<Guid, Outbound>();
        foreach (Outbound outbound in outbounds) current[outbound.Id] = outbound;

        var dropped = new List<Guid>();
        foreach (var kv in _instances)
        {
            // An outbound that has disappeared from the configuration cannot be routed to any more,
            // so its instance is only holding a subprocess or a socket open.
            bool stale = !current.TryGetValue(kv.Key, out Outbound? outbound)
                || !string.Equals(kv.Value.Signature, SignatureOf(outbound), StringComparison.Ordinal);
            if (!stale) continue;

            await DropAsync(kv.Key, "the configuration changed").ConfigureAwait(false);
            dropped.Add(kv.Key);
        }
        return dropped;
    }

    /// <summary>
    /// Throws away the instance of one outbound because whoever supervises it says it is finished:
    /// a VPN tunnel that has gone down for good, or one the user has switched off.
    /// </summary>
    /// <remarks>
    /// The next caller to ask for this outbound gets a freshly built instance, which is the point —
    /// a source built from a configuration that does not work stays broken however often it is
    /// started, and a configuration the user has since corrected is only picked up by building
    /// again.
    /// </remarks>
    public ValueTask DiscardAsync(Guid outboundId, string reason) => DropAsync(outboundId, reason);

    /// <summary>
    /// Turns IPv6 off (or back on) for the live instance of an outbound. Used when a connection
    /// teaches us that an Ipv6Support.Auto outbound has no IPv6 route: the instance then stops
    /// handing IPv6 addresses out on its own. A no-op for an outbound nothing has built yet, and
    /// for one whose answer cannot change after it is built.
    /// </summary>
    public void SetIpv6Support(Guid outboundId, bool supported) => Find(outboundId)?.SetIpv6Support(supported);

    private IOutboundInstance Build(Outbound outbound)
        => _factory.Create(outbound, _loggerFactory, _wireProxyPath);

    private async ValueTask DropAsync(Guid outboundId, string reason)
    {
        if (!_instances.TryRemove(outboundId, out Entry? entry)) return;

        _logger.LogDebug("outbound {Outbound} was dropped: {Reason}", outboundId, reason);
        await DisposeAsync(entry).ConfigureAwait(false);
        Raise(outboundId);
    }

    private static async ValueTask DisposeAsync(Entry entry)
    {
        IOutboundInstance? instance = entry.InstanceIfBuilt;
        if (instance is null) return;
        try { await instance.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    private void Raise(Guid outboundId)
    {
        try { InstanceDropped?.Invoke(outboundId); }
        catch { /* a broken subscriber must not stop the rest of a teardown */ }
    }

    /// <summary>Drops every instance, as the application closes.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Guid outboundId in new List<Guid>(_instances.Keys))
            await DropAsync(outboundId, "the application is closing").ConfigureAwait(false);
    }

    // What the container calls: ServiceProvider.Dispose refuses a singleton that offers DisposeAsync
    // alone, and this is registered as one. Blocking here cannot deadlock — every await underneath
    // is ConfigureAwait(false) — but it does hold the calling thread for as long as putting the
    // tunnels down takes, so the application goes through DisposeAsync instead.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // The instance plus what it will be built from, so a later configuration can be compared
    // against it without building anything.
    //
    // Built lazily so that adding one is free. ConcurrentDictionary.GetOrAdd may run its factory on
    // several threads at once and keep only one result; when the factory is what spawns wireproxy
    // or dials a tunnel, the copies it discards are not merely wasted work — nobody holds them, so
    // nobody ever shuts them down. Making the expensive part a Lazy means only the entry the
    // dictionary actually kept is ever built.
    private sealed class Entry
    {
        private readonly Lazy<IOutboundInstance> _instance;

        public Entry(string signature, Func<IOutboundInstance> build)
        {
            Signature = signature;
            _instance = new Lazy<IOutboundInstance>(build, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// What this entry will be built from. The instance carries the same stamp, but an entry
        /// nothing has needed yet has no instance to ask.
        /// </summary>
        public string Signature { get; }

        public IOutboundInstance Instance => _instance.Value;

        /// <summary>
        /// The instance if something has already asked for it, else null — for callers that must
        /// not cause one to be built.
        /// </summary>
        public IOutboundInstance? InstanceIfBuilt => _instance.IsValueCreated ? _instance.Value : null;
    }
}
