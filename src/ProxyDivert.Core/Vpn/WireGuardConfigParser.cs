using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using TqkLibrary.Proxy.Vpn.WireProxyCli;

namespace ProxyDivert.Core.Vpn;

// Reads a WireGuard .conf — the file a VPN provider hands out, unchanged — into the object model
// the wireproxy runner wants.
//
// Why parse it at all instead of passing the path straight through: wireproxy needs an extra
// [Socks5] section naming the local listener, and a provider's file never has one. Reading the
// file and letting the runner generate its own config means the user points at the file they
// already downloaded and nothing else. A file that DOES already have a [Socks5] section (someone
// wrote it for wireproxy) is used as-is instead — see ParseSocks5BindAddress.
//
// The format is INI-like: "[Section]" headers, "Key = value" lines, "#" or ";" comments, and lists
// separated by commas. Keys are matched case-insensitively because real files in the wild are not
// consistent about it.
public static class WireGuardConfigParser
{
    /// <summary>
    /// Reads a WireGuard configuration file. Throws <see cref="FormatException"/> when the file
    /// cannot make a usable tunnel, with a message naming what is missing.
    /// </summary>
    public static WireGuardConfig ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"WireGuard config not found: {path}", path);
        return Parse(File.ReadAllText(path));
    }

    public static WireGuardConfig Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var config = new WireGuardConfig();
        WireGuardPeer? peer = null;
        string section = string.Empty;

        foreach ((string key, string value, string currentSection) in ReadEntries(text))
        {
            // A header entry carries no key. Reacting to the header itself rather than to a change
            // of name is what makes two consecutive [Peer] sections two peers.
            if (key.Length == 0)
            {
                section = currentSection;
                if (section == "peer")
                {
                    peer = new WireGuardPeer();
                    config.Peers.Add(peer);
                }
                continue;
            }

            switch (section)
            {
                case "interface":
                    ApplyInterface(config.Interface, key, value);
                    break;
                case "peer" when peer != null:
                    ApplyPeer(peer, key, value);
                    break;
                // [Socks5] and anything else: not ours to interpret here.
            }
        }

        Validate(config);
        return config;
    }

    /// <summary>
    /// The BindAddress of a [Socks5] section, or null when the file has none. A file that already
    /// carries one is a wireproxy config: it is handed to wireproxy untouched, and this is where
    /// the tunnel will be listening.
    /// </summary>
    public static IPEndPoint? ParseSocks5BindAddress(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        foreach ((string key, string value, string section) in ReadEntries(text))
        {
            if (section != "socks5" || !key.Equals("bindaddress", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryParseEndPoint(value, out IPEndPoint? endPoint)) return endPoint;
            throw new FormatException($"[Socks5] BindAddress is not a valid host:port value: '{value}'");
        }
        return null;
    }

    public static IPEndPoint? ParseSocks5BindAddressFromFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"WireGuard config not found: {path}", path);
        return ParseSocks5BindAddress(File.ReadAllText(path));
    }

    // ---- parsing ------------------------------------------------------------------------------

    private static IEnumerable<(string Key, string Value, string Section)> ReadEntries(string text)
    {
        string section = string.Empty;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line[0] == '[' && line[line.Length - 1] == ']')
            {
                section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                // A section header is an entry too: it is what tells Parse a new [Peer] started,
                // even for a peer whose first key line is missing.
                yield return (string.Empty, string.Empty, section);
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0) continue;

            yield return (line.Substring(0, equals).Trim(), line.Substring(equals + 1).Trim(), section);
        }
    }

    // Comment markers only count at the start of a token, so a "#" inside a base64 key (it cannot
    // occur, but a password in a [Socks5] section can contain one) is left alone.
    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#');
        int semi = line.IndexOf(';');
        int cut = hash < 0 ? semi : semi < 0 ? hash : Math.Min(hash, semi);
        return cut < 0 ? line : line.Substring(0, cut);
    }

    private static void ApplyInterface(WireGuardInterface iface, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "privatekey": iface.PrivateKey = value; break;
            case "address": AddList(iface.Address, value); break;
            case "dns": AddList(iface.DNS, value); break;
            case "mtu":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mtu)) iface.MTU = mtu;
                break;
            // ListenPort, Table, PostUp/PostDown and friends are for the kernel driver; wireproxy
            // has no interface to run them on, so they are ignored rather than rejected.
        }
    }

    private static void ApplyPeer(WireGuardPeer peer, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "publickey": peer.PublicKey = value; break;
            case "presharedkey": peer.PresharedKey = value; break;
            case "endpoint": peer.Endpoint = value; break;
            case "allowedips": AddList(peer.AllowedIPs, value); break;
            case "persistentkeepalive":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keepalive))
                    peer.PersistentKeepalive = keepalive;
                break;
        }
    }

    private static void AddList(IList<string> target, string value)
    {
        foreach (string item in value.Split(','))
        {
            string trimmed = item.Trim();
            if (trimmed.Length > 0) target.Add(trimmed);
        }
    }

    private static void Validate(WireGuardConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Interface.PrivateKey))
            throw new FormatException("WireGuard config has no [Interface] PrivateKey.");
        if (config.Interface.Address.Count == 0)
            throw new FormatException("WireGuard config has no [Interface] Address.");

        WireGuardPeer? peer = config.Peers.FirstOrDefault();
        if (peer is null)
            throw new FormatException("WireGuard config has no [Peer] section.");
        if (string.IsNullOrWhiteSpace(peer.PublicKey))
            throw new FormatException("WireGuard config has a [Peer] without a PublicKey.");
        if (string.IsNullOrWhiteSpace(peer.Endpoint))
            throw new FormatException("WireGuard config has a [Peer] without an Endpoint.");
        // AllowedIPs is what tells wireproxy which destinations belong in the tunnel; a peer
        // without it would silently carry nothing.
        if (peer.AllowedIPs.Count == 0)
            throw new FormatException("WireGuard config has a [Peer] without AllowedIPs.");
    }

    // "1.2.3.4:1080", "[::1]:1080" or "host:1080" — wireproxy accepts all three, and a listener on
    // anything but loopback would expose the tunnel, so the caller checks that separately.
    private static bool TryParseEndPoint(string value, out IPEndPoint? endPoint)
    {
        endPoint = null;
        int colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1) return false;

        string host = value.Substring(0, colon).Trim().Trim('[', ']');
        if (!int.TryParse(value.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
            return false;
        if (port <= 0 || port > 65535) return false;

        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            endPoint = new IPEndPoint(address, port);
            return true;
        }
        // A name here is unusual but legal; resolve it once, at parse time.
        IPAddress[] resolved = Dns.GetHostAddresses(host);
        if (resolved.Length == 0) return false;
        endPoint = new IPEndPoint(resolved[0], port);
        return true;
    }
}
