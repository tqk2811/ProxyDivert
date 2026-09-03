using System;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Security.Cryptography;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Vpn;
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
public sealed class OutboundSourceFactory : IDisposable
{
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<Guid, IProxySource> _cache = new ConcurrentDictionary<Guid, IProxySource>();

    /// <summary>
    /// Where wireproxy.exe lives, for VPN outbounds. Null or empty means "look next to this
    /// executable, then on PATH" — which is what a user who dropped the binary in the tool's folder
    /// expects. One setting for the machine rather than one per outbound: it is the same binary
    /// whichever tunnel it runs.
    /// </summary>
    public string? WireProxyPath { get; set; }

    public OutboundSourceFactory(ILoggerFactory? loggerFactory = null, string? wireProxyPath = null)
    {
        _loggerFactory = loggerFactory;
        WireProxyPath = wireProxyPath;
    }

    public IProxySource GetOrCreate(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        return _cache.GetOrAdd(outbound.Id, _ => Create(outbound));
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
        if (_cache.TryGetValue(outboundId, out IProxySource? source))
            ApplyIpv6Support(source, supported);
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
        }
    }

    // Call after the user edits or removes an outbound.
    public void Invalidate(Guid outboundId)
    {
        if (_cache.TryRemove(outboundId, out IProxySource? source)) Dispose(source);
    }

    public void InvalidateAll()
    {
        foreach (var kv in _cache) Dispose(kv.Value);
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

    // A WireGuard tunnel run in user space by wireproxy, which exposes it as a loopback SOCKS5
    // listener. Nothing touches the OS: no TUN adapter, no route table, no second elevation prompt,
    // and — unlike the official client — other applications keep their normal network while this
    // one process goes through the VPN.
    //
    // Outbound.Url is the path to the .conf file. Two shapes are accepted:
    //   * the file a VPN provider gives you (only [Interface]/[Peer]) — it is read here and
    //     wireproxy gets a generated copy with a [Socks5] section on a private loopback port;
    //   * a file that already has a [Socks5] section — handed to wireproxy untouched.
    private IProxySource CreateVpn(Outbound outbound)
    {
        if (string.IsNullOrWhiteSpace(outbound.Url))
            throw new InvalidOperationException(
                $"VPN outbound '{outbound.Name}' has no configuration file. Point it at a WireGuard .conf.");

        string configPath = Environment.ExpandEnvironmentVariables(outbound.Url!.Trim().Trim('"'));
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

    private static void Dispose(IProxySource source)
    {
        try { (source as IDisposable)?.Dispose(); } catch { }
    }

    public void Dispose() => InvalidateAll();
}
