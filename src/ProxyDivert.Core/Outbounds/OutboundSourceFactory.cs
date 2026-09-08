using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Security.Cryptography;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Vpn;
using ProxyDivert.Core.Vpn.Client;
using ProxyDivert.Core.Vpn.Models;
using TqkLibrary.Proxy.Vpn.WireProxyCli;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Authentications;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.Proxy.ProxySources;

namespace ProxyDivert.Core.Outbounds;

// Builds the IProxySource for an outbound, and keeps ONE instance per outbound id.
//
// Sharing matters: an IProxySource is a factory of tunnels, not a connection, and some
// implementations keep state (SSH/WireGuard sessions) that must not be rebuilt per connection.
// Invalidate(id) drops the cached instance when the user edits or deletes an outbound, so the next
// connection picks up the new settings.
//
// Block is not represented here — a blocked connection is closed, never tunnelled.
public sealed class OutboundSourceFactory : IDisposable, IAsyncDisposable
{
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<Guid, CachedSource> _cache = new ConcurrentDictionary<Guid, CachedSource>();

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
    {
        _loggerFactory = loggerFactory;
        WireProxyPath = wireProxyPath;
    }

    public IProxySource GetOrCreate(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        return _cache.GetOrAdd(
            outbound.Id,
            _ => new CachedSource(OutboundSignature.Of(outbound, WireProxyPath), () => Create(outbound))).Source;
    }

    /// <summary>
    /// The live instance of an outbound, or null when nothing has needed it yet. Does not build
    /// one — a caller that wants it built asks <see cref="GetOrCreate"/>.
    /// </summary>
    public IProxySource? Find(Guid outboundId)
        => _cache.TryGetValue(outboundId, out CachedSource? cached) ? cached.SourceIfBuilt : null;

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
    /// handing IPv6 addresses to it on its own — for Direct that means name lookups return A
    /// records only, which is what makes "no IPv6 out there, use IPv4" actually happen.
    /// No-op for sources that do not expose the switch.
    /// </summary>
    public void SetIpv6Support(Guid outboundId, bool supported)
    {
        // SourceIfBuilt, not Source: this is what a failed connection teaches us, so the instance
        // always exists by now — and spawning one here just to configure it would be absurd.
        if (_cache.TryGetValue(outboundId, out CachedSource? cached) && cached.SourceIfBuilt is IProxySource built)
            ApplyIpv6Support(built, supported);
    }

    private static void ApplyIpv6Support(IProxySource source, bool supported)
    {
        // IProxySource only exposes IsSupportIpv6 as a getter; the concrete sources make it
        // settable. Socks4ProxySource is hard-wired to false — the protocol has no IPv6 at all.
        switch (source)
        {
            case LocalProxySource local: local.IsSupportIpv6 = supported; break;
            case HttpProxySource http: http.IsSupportIpv6 = supported; break;
            case Socks5ProxySource socks5: socks5.IsSupportIpv6 = supported; break;
            // The VPN tunnel takes the switch too, but it only ever narrows: a tunnel that got
            // no global IPv6 stays without one however this is set.
            case VpnClientProxySource vpn: vpn.IsSupportIpv6 = supported; break;
        }
    }

    // Call after the user edits or removes an outbound.
    public async ValueTask InvalidateAsync(Guid outboundId)
    {
        if (_cache.TryRemove(outboundId, out CachedSource? cached))
            await DisposeSourceAsync(cached.SourceIfBuilt).ConfigureAwait(false);
    }

    public async ValueTask InvalidateAllAsync()
    {
        foreach (var kv in _cache) await DisposeSourceAsync(kv.Value.SourceIfBuilt).ConfigureAwait(false);
        _cache.Clear();
    }

    // Builds a source without caching it — used by the UI's "test this outbound" button, so a
    // test never disturbs the instance live traffic is using.
    public IProxySource Create(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));

        IProxySource source = CreateCore(outbound);
        // Ipv6Support.Disabled is a statement about this way out, so it belongs on the source
        // itself: LocalProxySource then filters name lookups down to A records instead of handing
        // the connection an AAAA it cannot use.
        if (outbound.Ipv6Support == Ipv6Support.Disabled) ApplyIpv6Support(source, false);
        return source;
    }

    private IProxySource CreateCore(Outbound outbound)
    {
        switch (outbound.Kind)
        {
            case OutboundKind.Direct:
                return new LocalProxySource();

            case OutboundKind.HttpProxy:
            {
                Uri uri = ParseUri(outbound, "http");
                var source = new HttpProxySource(uri, _loggerFactory);
                if (HasCredential(outbound))
                    source.Credential = new ProxyCredential(outbound.Username!, outbound.Password!);
                return source;
            }

            case OutboundKind.Socks4:
            {
                Uri uri = ParseUri(outbound, "socks4");
                bool isSocks4a = uri.Scheme.Equals("socks4a", StringComparison.OrdinalIgnoreCase);
                // SOCKS4 authenticates with a user id only — there is no password in the protocol.
                return new Socks4ProxySource(ResolveEndPoint(uri), outbound.Username, _loggerFactory)
                {
                    IsUseSocks4A = isSocks4a,
                };
            }

            case OutboundKind.Socks5:
            {
                Uri uri = ParseUri(outbound, "socks5");
                IPEndPoint endPoint = ResolveEndPoint(uri);
                return HasCredential(outbound)
                    ? new Socks5ProxySource(endPoint, new ProxyCredential(outbound.Username!, outbound.Password!), _loggerFactory)
                    : new Socks5ProxySource(endPoint, _loggerFactory);
            }

            case OutboundKind.Block:
                throw new InvalidOperationException(
                    "Block has no proxy source — the caller must close the connection instead of tunnelling it.");

            case OutboundKind.Vpn:
                return CreateVpn(outbound);

            default:
                throw new ArgumentOutOfRangeException(nameof(outbound), outbound.Kind, "Unknown outbound kind");
        }
    }

    // A VPN, run by one of two engines. Both keep the rest of the machine on its normal network:
    // no TUN adapter, no route table, no second elevation prompt.
    //
    // Which engine is decided by the outbound's URL and its protocol box, and VpnProfileReader is
    // the only thing that reads them. A WireGuard .conf still goes to wireproxy, exactly as it did
    // before any of the other protocols existed; everything else is dialled inside this process by
    // TqkLibrary.VpnClient, which also means those tunnels carry UDP.
    private IProxySource CreateVpn(Outbound outbound)
    {
        VpnProfile profile = VpnProfileReader.Read(outbound);
        if (!profile.RunsOnWireProxy)
        {
            return new VpnClientProxySource(
                profile, outbound.Ipv6Support != Ipv6Support.Disabled, _loggerFactory);
        }

        return CreateWireProxyVpn(outbound, profile.ConfigPath!);
    }

    // The wireproxy engine: a subprocess running the WireGuard tunnel in user space and exposing it
    // as a loopback SOCKS5 listener. Two shapes of .conf are accepted:
    //   * the file a VPN provider gives you (only [Interface]/[Peer]) — it is read here and
    //     wireproxy gets a generated copy with a [Socks5] section on a private loopback port;
    //   * a file that already has a [Socks5] section — handed to wireproxy untouched.
    private IProxySource CreateWireProxyVpn(Outbound outbound, string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"VPN outbound '{outbound.Name}': config file not found.", configPath);

        var options = new WireGuardOptions
        {
            BinaryPath = string.IsNullOrWhiteSpace(WireProxyPath) ? null : WireProxyPath,
            // wireproxy's SOCKS5 is TCP-only, so UDP must not be advertised: the router downgrades
            // "UDP through this outbound" to Block rather than letting the datagrams out direct.
            IsSupportUdp = false,
            IsSupportIpv6 = outbound.Ipv6Support != Ipv6Support.Disabled,
        };

        string text = File.ReadAllText(configPath);
        IPEndPoint? existingSocks5 = WireGuardConfigParser.ParseSocks5BindAddress(text);
        if (existingSocks5 != null)
        {
            options.ConfigFilePath = configPath;
            options.ExternalSocks5Endpoint = existingSocks5;
        }
        else
        {
            options.Config = WireGuardConfigParser.Parse(text);
            // A loopback SOCKS5 listener with no credentials is usable by every process on the
            // machine — including the ones the user is deliberately keeping OUT of the tunnel.
            // A random per-instance credential closes that without asking the user for anything.
            options.Socks5Username = "pd";
            options.Socks5Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        }

        return new WireGuardProxySource(options, _loggerFactory);
    }

    private static bool HasCredential(Outbound outbound)
        => !string.IsNullOrEmpty(outbound.Username) && !string.IsNullOrEmpty(outbound.Password);

    private static Uri ParseUri(Outbound outbound, string defaultScheme)
    {
        if (string.IsNullOrWhiteSpace(outbound.Url))
            throw new InvalidOperationException($"Outbound '{outbound.Name}' has no URL.");

        string raw = outbound.Url!.Trim();
        // Accept a bare "host:port" — that is how proxy lists are usually pasted.
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = defaultScheme + "://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
            throw new FormatException($"Outbound '{outbound.Name}' has an invalid URL: {outbound.Url}");
        return uri;
    }

    // SOCKS sources take an endpoint rather than a URI, so a host name has to be resolved here.
    // This lookup uses the machine's own DNS: it resolves the PROXY's address, not the traffic's
    // destination, so it reveals nothing about what the user is browsing.
    private static IPEndPoint ResolveEndPoint(Uri uri)
    {
        if (uri.Port <= 0)
            throw new FormatException($"Proxy URL must include a port: {uri}");

        // Uri.Host keeps the brackets of an IPv6 literal ("[::1]"), which IPAddress.TryParse
        // rejects — without trimming them an IPv6 proxy address would be sent to the resolver as
        // if it were a host name.
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? ip))
            return new IPEndPoint(ip, uri.Port);

        IPAddress[] addresses = System.Net.Dns.GetHostAddresses(uri.Host);
        if (addresses.Length == 0)
            throw new InvalidOperationException($"Cannot resolve proxy host '{uri.Host}'.");
        return new IPEndPoint(addresses[0], uri.Port);
    }

    /// <summary>
    /// Releases a source, whatever it happens to be underneath. Public because <see cref="Create"/>
    /// <summary>
    /// Releases a source, whatever it happens to be underneath. Public because <see cref="Create"/>
    /// hands ownership to its caller, and a wireproxy-backed source that nobody releases leaves a
    /// subprocess running for the life of the app.
    /// </summary>
    /// <remarks>
    /// Awaited rather than blocked on. Releasing a VPN source means putting a tunnel down and
    /// waiting for the driver to finish, and the caller that used to block on that was the reason
    /// the source could only give it five seconds before walking away.
    /// </remarks>
    public static async ValueTask DisposeSourceAsync(IProxySource? source)
    {
        if (source is null) return;
        try { await source.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync() => await InvalidateAllAsync().ConfigureAwait(false);

    // What the container calls: ServiceProvider.Dispose refuses a singleton that offers DisposeAsync
    // alone, and this is registered as one. Blocking here cannot deadlock — every await underneath
    // is ConfigureAwait(false) — but it does hold the calling thread for as long as putting the
    // tunnels down takes, so the application goes through DisposeAsync instead.
    public void Dispose() => InvalidateAllAsync().AsTask().GetAwaiter().GetResult();

    // The instance plus what it was built from, so a later configuration can be compared against
    // it without rebuilding anything.
    //
    // Built lazily so that constructing this is free. ConcurrentDictionary.GetOrAdd may run its
    // factory on several threads at once and keep only one result; when the factory is what spawns
    // wireproxy or dials a tunnel, the copies it discards are not merely wasted work — nobody holds
    // them, so nobody ever shuts them down. Making the expensive part a Lazy means only the entry
    // the dictionary actually kept is ever built.
    private sealed class CachedSource
    {
        private readonly Lazy<IProxySource> _source;

        public CachedSource(string signature, Func<IProxySource> build)
        {
            Signature = signature;
            _source = new Lazy<IProxySource>(build, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public string Signature { get; }

        /// <summary>The instance, built on first use.</summary>
        public IProxySource Source => _source.Value;

        /// <summary>
        /// The instance if something has already asked for it, else null — for callers that must
        /// not cause one to be built.
        /// </summary>
        public IProxySource? SourceIfBuilt => _source.IsValueCreated ? _source.Value : null;
    }
}
