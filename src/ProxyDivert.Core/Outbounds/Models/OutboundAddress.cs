using System;
using ProxyDivert.Core.Outbounds.Enums;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Vpn.Enums;

namespace ProxyDivert.Core.Outbounds.Models;

/// <summary>
/// What an outbound's one address box means, read once.
/// </summary>
/// <remarks>
/// The box holds four different things depending on the outbound — a proxy, a VPN server, a
/// provider's configuration file, or a little ini naming one — and every part of the application
/// that needed one of them used to work it out again from the string: the proxy builders, the VPN
/// profile reader, the signature that decides whether a running tunnel is still the right one, and
/// the command line. Four readings of the same box is four chances to disagree about it, and the
/// signature already did: it stamped the file for every VPN outbound, including the ones whose box
/// holds an address and names no file at all.
///
/// This answers only what the box IS. What to do about it stays where it was: which VPN protocol a
/// scheme means, whether a file may be run by wireproxy, how a host name becomes an endpoint. Those
/// are decisions about the outbound, and they are not made here.
///
/// Nothing here touches the disk. The routing path asks whether a VPN can carry UDP once per
/// connection, and that question comes down to what this box says — so it must not become a file
/// system call. Whether the file is actually there is asked later, by the reader that opens it.
/// </remarks>
public sealed class OutboundAddress
{
    private OutboundAddress(
        OutboundAddressKind kind, string text, Uri? uri, string? host, int port, string? hub, string? path)
    {
        Kind = kind;
        Text = text;
        Uri = uri;
        Host = host;
        Port = port;
        Hub = hub;
        Path = path;
    }

    public OutboundAddressKind Kind { get; }

    /// <summary>
    /// The box with environment variables expanded and any stray quotes and spaces taken off — what
    /// the user meant, rather than what they pasted.
    /// </summary>
    public string Text { get; }

    /// <summary>The address, for the two endpoint kinds. Null for the two file kinds.</summary>
    public Uri? Uri { get; }

    /// <summary>The server, with an IPv6 literal's brackets already off. Null for the file kinds.</summary>
    public string? Host { get; }

    /// <summary>The port, or 0 when the box did not give one.</summary>
    public int Port { get; }

    /// <summary>The SoftEther virtual hub, taken from the path of the address. Null when there is none.</summary>
    public string? Hub { get; }

    /// <summary>The file, for the two file kinds. Null for the endpoint kinds.</summary>
    public string? Path { get; }

    public bool IsFile => Kind is OutboundAddressKind.VpnConfigFile or OutboundAddressKind.VpnIniFile;

    /// <summary>Whether an outbound of this kind is configured with an address at all.</summary>
    /// <remarks>
    /// Direct and Block are not ways to somewhere — one is the machine's own stack and the other is
    /// the absence of one — so their box means nothing and is kept empty.
    /// </remarks>
    public static bool IsWanted(OutboundKind kind)
        => kind is OutboundKind.HttpProxy or OutboundKind.Socks4 or OutboundKind.Socks5 or OutboundKind.Vpn;

    /// <summary>
    /// Reads the box. False with a reason in <paramref name="error"/> when it cannot be read, and
    /// false with no reason for the kinds that want no address.
    /// </summary>
    /// <param name="protocol">
    /// The protocol chosen by hand on the outbound. It only matters for one case: a VPN box holding
    /// a bare "host:port" with no scheme is an address when the user has already said which dialled
    /// protocol they mean, and a file path otherwise.
    /// </param>
    public static bool TryParse(
        OutboundKind kind,
        string? url,
        VpnProtocol protocol,
        out OutboundAddress? address,
        out string? error)
    {
        address = null;
        error = null;

        if (!IsWanted(kind)) return false;

        string text = Expand(url);
        if (text.Length == 0)
        {
            error = kind == OutboundKind.Vpn
                ? "has nothing in its address box. Point it at a configuration file, or at a server "
                  + "such as sstp://vpn.example.com:443."
                : "has nothing in its address box. Type the proxy's address, such as "
                  + "socks5://127.0.0.1:1080.";
            return false;
        }

        return kind == OutboundKind.Vpn
            ? TryParseVpn(text, protocol, out address, out error)
            : TryParseProxy(kind, text, out address, out error);
    }

    /// <summary>
    /// Which kind of proxy an address names, for a caller holding the address and not the kind.
    /// </summary>
    /// <remarks>
    /// That caller is the command line, where the proxy is one argument and nothing else says what
    /// sort it is. It reads the scheme the same way <see cref="TryParse"/> writes it, which is the
    /// point of it being here rather than a fifth reading of the same string.
    /// </remarks>
    public static bool TryReadProxyKind(string? url, out OutboundKind kind)
    {
        kind = OutboundKind.Direct;

        string text = Expand(url);
        int mark = text.IndexOf("://", StringComparison.Ordinal);
        if (mark <= 0) return false;

        switch (text.Substring(0, mark).ToLowerInvariant())
        {
            case "http":
            case "https":
                kind = OutboundKind.HttpProxy;
                return true;
            case "socks4":
            case "socks4a":
                kind = OutboundKind.Socks4;
                return true;
            case "socks":
            case "socks5":
                kind = OutboundKind.Socks5;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The exception for a box that could not be read, for the callers whose contract is to throw.
    /// </summary>
    /// <param name="subject">How the message names the outbound, e.g. <c>Outbound 'work'</c>.</param>
    /// <remarks>
    /// Nothing typed is a configuration that was never finished; something unreadable typed is a
    /// format mistake. Two different things to tell apart, and not something each caller should
    /// decide again on its own.
    /// </remarks>
    public static Exception Unreadable(string subject, string? url, string? problem)
        => string.IsNullOrWhiteSpace(url)
            ? new InvalidOperationException($"{subject} {problem}")
            : new FormatException($"{subject} {problem}");

    /// <summary>
    /// Environment variables and surrounding quotes are both things people paste in without meaning
    /// to. Expanding here saves every caller from remembering, and an expansion that fails leaves
    /// the text as typed rather than losing it.
    /// </summary>
    public static string Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Trim(); }
        catch { return value.Trim().Trim('"'); }
    }

    private static bool TryParseProxy(
        OutboundKind kind, string text, out OutboundAddress? address, out string? error)
    {
        address = null;
        error = null;

        // A bare "host:port" is how proxy lists are pasted, so the scheme the outbound's own kind
        // implies is filled in rather than demanded.
        string withScheme = text.Contains("://", StringComparison.Ordinal)
            ? text
            : DefaultScheme(kind) + "://" + text;

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out Uri? uri))
        {
            error = $"has an address that cannot be read: {text}";
            return false;
        }

        string host = uri.Host.Trim('[', ']');
        if (host.Length == 0)
        {
            error = $"has an address with no host in it: {text}";
            return false;
        }

        // Uri fills in 80 for http and leaves -1 for a scheme it does not know, which is every SOCKS
        // one. A SOCKS proxy with no port cannot be dialled at all, and the connection that finds
        // that out is one the user is waiting on — so it is said here, at the box.
        int port = uri.Port > 0 ? uri.Port : 0;
        if (port == 0)
        {
            error = $"has an address with no port: {text}";
            return false;
        }

        address = new OutboundAddress(
            OutboundAddressKind.ProxyEndpoint, text, uri, host, port, hub: null, path: null);
        return true;
    }

    private static bool TryParseVpn(
        string text, VpnProtocol protocol, out OutboundAddress? address, out string? error)
    {
        address = null;
        error = null;

        string withScheme = text;
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            // No scheme. Either the user has already said which dialled protocol they mean — and
            // then the box is the bare "host" or "host:port" a server is usually pasted as — or
            // this is a path. A path is excluded first, because otherwise "D:\vpn\jp.ovpn" would be
            // dialled as the host "d" and the failure would say nothing about what went wrong.
            if (!IsDialled(protocol) || LooksLikePath(text))
            {
                address = new OutboundAddress(
                    text.EndsWith(".vpn", StringComparison.OrdinalIgnoreCase)
                        ? OutboundAddressKind.VpnIniFile
                        : OutboundAddressKind.VpnConfigFile,
                    text, uri: null, host: null, port: 0, hub: null, path: text);
                return true;
            }

            withScheme = SchemeFor(protocol) + "://" + text;
        }

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out Uri? uri))
        {
            error = $"has an address that cannot be read: {text}";
            return false;
        }

        string host = uri.Host.Trim('[', ']');
        if (host.Length == 0)
        {
            error = $"has no server in its address: {text}";
            return false;
        }

        // The virtual hub SoftEther needs is a name rather than a secret, so it goes in the visible
        // box, and the path of the address is the natural place for it. Read for every scheme and
        // used by the one that wants it: whether it is required is the profile reader's business.
        string? hub = uri.AbsolutePath.Trim('/');
        if (hub.Length == 0) hub = null;

        address = new OutboundAddress(
            OutboundAddressKind.VpnEndpoint, text, uri, host, uri.Port > 0 ? uri.Port : 0, hub, path: null);
        return true;
    }

    // A directory separator or a drive letter. Deliberately not asking the file system: this runs
    // before anyone knows the file exists, and on the routing path, and "the path you typed is
    // wrong" is a better message than "that host does not resolve".
    private static bool LooksLikePath(string text)
        => text.IndexOf('\\') >= 0
           || text.IndexOf('/') >= 0
           || (text.Length > 1 && text[1] == ':');

    // True for the VPN protocols that take a server and credentials rather than a provider's file.
    private static bool IsDialled(VpnProtocol protocol)
        => protocol is VpnProtocol.Sstp or VpnProtocol.L2tpIpsec or VpnProtocol.Ikev2
            or VpnProtocol.SoftEther;

    private static string SchemeFor(VpnProtocol protocol) => protocol switch
    {
        VpnProtocol.Sstp => "sstp",
        VpnProtocol.L2tpIpsec => "l2tp",
        VpnProtocol.Ikev2 => "ikev2",
        VpnProtocol.SoftEther => "softether",
        _ => "vpn",
    };

    private static string DefaultScheme(OutboundKind kind) => kind switch
    {
        OutboundKind.Socks4 => "socks4",
        OutboundKind.Socks5 => "socks5",
        _ => "http",
    };

    public override string ToString() => Kind + ":" + (IsFile ? Path : Uri?.ToString() ?? Text);
}
