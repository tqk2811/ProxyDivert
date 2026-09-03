using System;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Routing.Enums;
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

    public OutboundSourceFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    public IProxySource GetOrCreate(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        return _cache.GetOrAdd(outbound.Id, _ => Create(outbound));
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
                throw new NotSupportedException(
                    "VPN outbounds arrive in phase 2 (TqkLibrary.VpnClient wrapped as an IProxySource).");

            default:
                throw new ArgumentOutOfRangeException(nameof(outbound), outbound.Kind, "Unknown outbound kind");
        }
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

        if (IPAddress.TryParse(uri.Host, out IPAddress? ip))
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
