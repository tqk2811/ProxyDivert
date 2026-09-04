using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;

namespace ProxyDivert.Core.Vpn;

// Turns one outbound's URL box into a VpnProfile.
//
// The six protocols are not configured the same way. OpenVPN and WireGuard are handed the file the
// provider gave you; SSTP, L2TP/IPsec, IKEv2 and SoftEther have no standard client file at all and
// are dialled with a server address plus credentials. So the URL box accepts both shapes, told
// apart by whether it contains "://":
//
//   sstp://vpn.example.com:443            an address — credentials come from the outbound's own
//   l2tp://vpn.example.com                boxes, where the password and the pre-shared key are
//   ikev2://vpn.example.com               encrypted with DPAPI like every other password here
//   softether://vpn.example.com:443/HUB
//
//   D:\vpn\jp.ovpn                        a file the provider gave you
//   D:\vpn\wg0.conf
//   D:\vpn\office.vpn                     a small ini for the address-based protocols, for anyone
//                                         who would rather keep them in a file
//
// The Outbound.VpnProtocol box overrides the guess. It has one job beyond fixing a wrong guess: a
// .conf can be run two ways — by wireproxy (the default, unchanged from before) or in this process
// by TqkLibrary.VpnClient — and only the user can say which they want.
public static class VpnProfileReader
{
    private const int DefaultTlsPort = 443;

    /// <summary>
    /// Reads the outbound's URL, and the file it points at when there is one, into a dialable
    /// profile. Throws with a message naming the outbound when the settings do not add up.
    /// </summary>
    public static VpnProfile Read(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));

        string raw = Expand(outbound.Url);
        if (raw.Length == 0)
            throw new InvalidOperationException(
                $"VPN outbound '{outbound.Name}' has nothing in its URL box. Point it at a configuration "
                + "file, or at a server such as sstp://vpn.example.com:443.");

        VpnProtocol declared = outbound.VpnProtocol;

        if (raw.Contains("://", StringComparison.Ordinal))
            return FromAddress(outbound, raw, declared);

        // No scheme, but the user has already said it is one of the address-based protocols: then
        // the box is a bare "host" or "host:port", which is how a server is usually pasted. A path
        // is excluded first — otherwise "D:\vpn\jp.ovpn" would be dialled as the host "d", and the
        // resulting failure would say nothing about what actually went wrong.
        if (IsDialled(declared) && !LooksLikePath(raw))
            return FromAddress(outbound, SchemeFor(declared) + "://" + raw, declared);

        return FromFile(outbound, raw, declared);
    }

    // A directory separator or a drive letter. Not asking the file system: this runs before we know
    // the file exists, and "the path you typed is wrong" is a better message than "that host does
    // not resolve".
    private static bool LooksLikePath(string raw)
        => raw.IndexOf('\\') >= 0
           || raw.IndexOf('/') >= 0
           || (raw.Length > 1 && raw[1] == ':');

    /// <summary>
    /// Whether this outbound's tunnel is run by the external wireproxy binary, decided WITHOUT
    /// touching the disk.
    /// </summary>
    /// <remarks>
    /// It is asked on the routing path, once per connection, to work out whether the outbound can
    /// carry UDP — wireproxy's SOCKS5 is TCP-only while an in-process tunnel is not. Reading the
    /// configuration file there would put a file system call in front of every connection, so the
    /// question is answered from the URL alone. That is also why a .vpn file may not name wireproxy:
    /// it would be a claim this function cannot see.
    /// </remarks>
    public static bool RunsOnWireProxy(VpnProtocol protocol, string? url)
    {
        if (protocol == VpnProtocol.WireGuardWireProxy) return true;
        if (protocol != VpnProtocol.Auto) return false;

        string raw = Expand(url);
        if (raw.Length == 0 || raw.Contains("://", StringComparison.Ordinal)) return false;

        // A bare .conf is a WireGuard file, and by default those keep going through wireproxy so
        // that configurations made before any of this existed behave exactly as they did.
        return raw.EndsWith(".conf", StringComparison.OrdinalIgnoreCase);
    }

    private static VpnProfile FromAddress(Outbound outbound, string url, VpnProtocol declared)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new FormatException($"VPN outbound '{outbound.Name}' has an unreadable address: {outbound.Url}");

        VpnProtocol detected = FromScheme(uri.Scheme);
        VpnProtocol protocol = declared == VpnProtocol.Auto ? detected : declared;

        if (protocol == VpnProtocol.Auto)
            throw new FormatException(
                $"VPN outbound '{outbound.Name}': '{uri.Scheme}' is not a VPN this tool speaks. Use "
                + "sstp, l2tp, ikev2 or softether — or pick the protocol by hand.");

        if (!IsDialled(protocol))
            throw new InvalidOperationException(
                $"VPN outbound '{outbound.Name}': {protocol} is configured from a file, so the URL box "
                + "must hold a path rather than an address.");

        string host = uri.Host.Trim('[', ']');
        if (host.Length == 0)
            throw new FormatException($"VPN outbound '{outbound.Name}' has no server in its address.");

        // Only SSTP and SoftEther have a port worth choosing; L2TP and IKEv2 are fixed by their
        // protocols (UDP 500/4500, and L2TP inside), so a port there would be ignored anyway.
        int port = uri.Port > 0 ? uri.Port : DefaultTlsPort;

        // SoftEther needs the virtual hub as well, and the path is the natural place for it —
        // it is a name, not a secret, so it belongs in the visible box.
        string? hub = protocol == VpnProtocol.SoftEther ? uri.AbsolutePath.Trim('/') : null;
        if (protocol == VpnProtocol.SoftEther && string.IsNullOrEmpty(hub))
            throw new InvalidOperationException(
                $"VPN outbound '{outbound.Name}': SoftEther needs the virtual hub, as in "
                + "softether://vpn.example.com:443/VPN.");

        return new VpnProfile
        {
            Protocol = protocol,
            Host = host,
            Port = port,
            Hub = hub,
            Username = Blank(outbound.Username),
            Password = Blank(outbound.Password),
            PreSharedKey = Blank(outbound.PreSharedKey),
        };
    }

    private static VpnProfile FromFile(Outbound outbound, string path, VpnProtocol declared)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"VPN outbound '{outbound.Name}': configuration file not found.", path);

        if (path.EndsWith(".vpn", StringComparison.OrdinalIgnoreCase))
            return FromIniFile(outbound, path, declared);

        VpnProtocol detected = Sniff(path);
        VpnProtocol protocol = declared == VpnProtocol.Auto ? detected : declared;

        if (protocol == VpnProtocol.Auto)
            throw new InvalidOperationException(
                $"VPN outbound '{outbound.Name}': cannot tell what '{Path.GetFileName(path)}' is. Give it "
                + "a .ovpn or .conf extension, or pick the protocol by hand.");

        if (IsDialled(protocol))
            throw new InvalidOperationException(
                $"VPN outbound '{outbound.Name}': {protocol} is dialled by address, so the URL box must "
                + $"hold something like {SchemeFor(protocol)}://vpn.example.com rather than a file path.");

        return new VpnProfile
        {
            Protocol = protocol,
            ConfigPath = path,
            // OpenVPN profiles often want a user name and password on top of the file; WireGuard
            // never does, and simply ignores them.
            Username = Blank(outbound.Username),
            Password = Blank(outbound.Password),
        };
    }

    // The little ini for anyone who would rather keep an address-based VPN in a file than in the
    // outbound row. Deliberately plain: one section, one key per line.
    private static VpnProfile FromIniFile(Outbound outbound, string path, VpnProtocol declared)
    {
        Dictionary<string, string> values = ReadIni(path);

        VpnProtocol protocol = declared;
        if (protocol == VpnProtocol.Auto)
        {
            if (!values.TryGetValue("protocol", out string? name))
                throw new InvalidOperationException(
                    $"VPN outbound '{outbound.Name}': '{Path.GetFileName(path)}' has no 'Protocol =' line.");

            protocol = FromScheme(name);
            if (protocol == VpnProtocol.Auto)
                throw new InvalidOperationException(
                    $"VPN outbound '{outbound.Name}': '{name}' in '{Path.GetFileName(path)}' is not a VPN "
                    + "this tool speaks.");
            if (protocol == VpnProtocol.WireGuardWireProxy)
                throw new InvalidOperationException(
                    $"VPN outbound '{outbound.Name}': a .vpn file cannot ask for wireproxy — point the URL "
                    + "box straight at the .conf instead.");
        }

        // A secret in the outbound's own box is encrypted at rest; one written into this file is
        // not. So the boxes win where they are filled in, and the file only fills the gaps.
        string? user = Blank(outbound.Username) ?? Value(values, "user", "username");
        string? pass = Blank(outbound.Password) ?? Value(values, "pass", "password");
        string? psk = Blank(outbound.PreSharedKey) ?? Value(values, "psk", "presharedkey");

        if (!IsDialled(protocol))
        {
            string? config = Value(values, "config", "configpath")
                ?? throw new InvalidOperationException(
                    $"VPN outbound '{outbound.Name}': {protocol} is configured from a file, so "
                    + $"'{Path.GetFileName(path)}' needs a 'Config =' line pointing at it.");

            // Relative to the .vpn file, so a folder holding both can be moved as a unit.
            string resolved = Path.IsPathRooted(config)
                ? config
                : Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, config);
            if (!File.Exists(resolved))
                throw new FileNotFoundException(
                    $"VPN outbound '{outbound.Name}': the file named by 'Config =' was not found.", resolved);

            return new VpnProfile
            {
                Protocol = protocol,
                ConfigPath = resolved,
                Username = user,
                Password = pass,
            };
        }

        string host = Value(values, "host", "server")
            ?? throw new InvalidOperationException(
                $"VPN outbound '{outbound.Name}': '{Path.GetFileName(path)}' has no 'Host =' line.");

        int port = DefaultTlsPort;
        if (Value(values, "port") is string portText
            && int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0)
        {
            port = parsed;
        }

        string? hub = Value(values, "hub");
        if (protocol == VpnProtocol.SoftEther && string.IsNullOrEmpty(hub))
            throw new InvalidOperationException(
                $"VPN outbound '{outbound.Name}': SoftEther needs a 'Hub =' line in "
                + $"'{Path.GetFileName(path)}'.");

        return new VpnProfile
        {
            Protocol = protocol,
            Host = host,
            Port = port,
            Hub = hub,
            Username = user,
            Password = pass,
            PreSharedKey = psk,
            SoftEtherWatermarkPath = Value(values, "watermark"),
        };
    }

    // Extension first, then a look inside — an .ovpn is unmistakable, and a WireGuard file is the
    // only one of these with an [Interface] section.
    private static VpnProtocol Sniff(string path)
    {
        if (path.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)) return VpnProtocol.OpenVpn;

        string text;
        try { text = File.ReadAllText(path); }
        catch { return VpnProtocol.Auto; }

        if (text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
            return VpnProtocol.WireGuardWireProxy;
        if (text.Contains("\nremote ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
        {
            return VpnProtocol.OpenVpn;
        }

        return VpnProtocol.Auto;
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            if (key.Length != 0) values[key] = value;
        }
        return values;
    }

    private static string? Value(Dictionary<string, string> values, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    // True for the protocols that take a server and credentials rather than a provider's file.
    private static bool IsDialled(VpnProtocol protocol)
        => protocol is VpnProtocol.Sstp or VpnProtocol.L2tpIpsec or VpnProtocol.Ikev2 or VpnProtocol.SoftEther;

    private static string SchemeFor(VpnProtocol protocol) => protocol switch
    {
        VpnProtocol.Sstp => "sstp",
        VpnProtocol.L2tpIpsec => "l2tp",
        VpnProtocol.Ikev2 => "ikev2",
        VpnProtocol.SoftEther => "softether",
        _ => "vpn",
    };

    private static VpnProtocol FromScheme(string scheme) => scheme.Trim().ToLowerInvariant() switch
    {
        "sstp" => VpnProtocol.Sstp,
        "l2tp" or "l2tp-ipsec" or "l2tpipsec" => VpnProtocol.L2tpIpsec,
        "ikev2" or "ike2" => VpnProtocol.Ikev2,
        "softether" or "se" => VpnProtocol.SoftEther,
        "openvpn" or "ovpn" => VpnProtocol.OpenVpn,
        "wireguard" or "wg" => VpnProtocol.WireGuard,
        "wireproxy" or "wireguard-wireproxy" => VpnProtocol.WireGuardWireProxy,
        _ => VpnProtocol.Auto,
    };

    // Environment variables and surrounding quotes are both things people paste in without meaning
    // to; expanding here keeps every caller from having to remember.
    internal static string Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Trim(); }
        catch { return value.Trim().Trim('"'); }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
