using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing.Compiled;

/// <summary>
/// Turns one (matcher, pattern) pair into an <see cref="IHostPredicate"/>.
/// </summary>
/// <remarks>
/// A target has up to four facets and each matcher looks at exactly one of them:
/// <list type="bullet">
///   <item>host — the SNI / Host header / reverse-DNS name; may be absent</item>
///   <item>ip — always known</item>
///   <item>port — always known</item>
///   <item>protocol — always known, and known EARLIEST: before the handshake, before any name</item>
/// </list>
/// A name-based matcher never falls back to the IP text: matching "10.0.0.1" against a DomainSuffix
/// rule would be a coincidence, not an intent.
///
/// A pattern that does not parse compiles to <see cref="Never"/> and returns a message. Never null,
/// and never a throw: this is called from the save path, where one bad row must not stop the other
/// rules from being applied, and the row is reported instead.
/// </remarks>
public static class HostPredicate
{
    /// <summary>A predicate that matches nothing. What an unusable pattern compiles to.</summary>
    public static IHostPredicate Never { get; } = new NeverPredicate();

    public static IHostPredicate Compile(HostMatcherType matcher, string? pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "the pattern is empty";
            return Never;
        }

        string trimmed = pattern!.Trim();
        switch (matcher)
        {
            case HostMatcherType.IpCidr:
                return CompileCidr(trimmed, out error);

            case HostMatcherType.Port:
                return CompilePort(trimmed, out error);

            case HostMatcherType.Protocol:
                if (trimmed.Equals("udp", StringComparison.OrdinalIgnoreCase)) return new ProtocolPredicate(isUdp: true);
                if (trimmed.Equals("tcp", StringComparison.OrdinalIgnoreCase)) return new ProtocolPredicate(isUdp: false);
                // A typo must not silently claim the traffic the rule was meant to exclude.
                error = "expected \"tcp\" or \"udp\"";
                return Never;

            case HostMatcherType.Wildcard:
                return CompileRegex(WildcardToRegex(trimmed), out error);

            case HostMatcherType.Regex:
                return CompileRegex(trimmed, out error);

            case HostMatcherType.Equals:
            case HostMatcherType.StartsWith:
            case HostMatcherType.EndsWith:
            case HostMatcherType.Contains:
            case HostMatcherType.DomainSuffix:
                return new NamePredicate(matcher, trimmed);

            default:
                error = $"unknown matcher {matcher}";
                return Never;
        }
    }

    private static string WildcardToRegex(string pattern)
        => "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

    private static IHostPredicate CompileRegex(string pattern, out string? error)
    {
        error = null;
        try
        {
            // Compiled: the same expression is run once per rule per connection for as long as the
            // configuration stands, and the set of expressions is as small as the user's rule list.
            //
            // Deliberately no match timeout. A timeout would turn a slow expression into a rule
            // that quietly stops applying while the machine is busy, which is the exact failure the
            // process-rule regex budget already cost us once: traffic leaving unredirected with
            // nothing on screen to say why.
            return new RegexPredicate(new Regex(
                pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return Never;
        }
    }

    // "1.2.3.4" (exact) or "10.0.0.0/8". IPv4 and IPv6 both work; families never cross-match.
    private static IHostPredicate CompileCidr(string pattern, out string? error)
    {
        error = null;
        string[] parts = pattern.Split('/');
        if (!IPAddress.TryParse(parts[0], out IPAddress? network))
        {
            error = $"\"{parts[0]}\" is not an IP address";
            return Never;
        }

        byte[] networkBytes = network.GetAddressBytes();
        int prefix = networkBytes.Length * 8;
        if (parts.Length > 1)
        {
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix)
                || prefix < 0 || prefix > networkBytes.Length * 8)
            {
                error = $"\"{parts[1]}\" is not a prefix length for {network.AddressFamily}";
                return Never;
            }
        }

        // Masked here rather than per connection, which also makes "10.1.2.3/8" behave as the
        // "10.0.0.0/8" the user meant instead of depending on which side is masked at match time.
        for (int i = 0; i < networkBytes.Length; i++)
        {
            int bitsLeft = prefix - (i * 8);
            networkBytes[i] = bitsLeft >= 8 ? networkBytes[i]
                : bitsLeft <= 0 ? (byte)0
                : (byte)(networkBytes[i] & (0xFF << (8 - bitsLeft)));
        }

        return new CidrPredicate(network.AddressFamily, networkBytes, prefix);
    }

    // "443" or "8000-8100".
    private static IHostPredicate CompilePort(string pattern, out string? error)
    {
        error = null;
        int dash = pattern.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(pattern, NumberStyles.Integer, CultureInfo.InvariantCulture, out int single))
            {
                error = $"\"{pattern}\" is not a port number";
                return Never;
            }
            return new PortPredicate(single, single);
        }

        if (!int.TryParse(pattern.Substring(0, dash).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int from)
            || !int.TryParse(pattern.Substring(dash + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int to))
        {
            error = $"\"{pattern}\" is not a port range";
            return Never;
        }

        return new PortPredicate(Math.Min(from, to), Math.Max(from, to));
    }

    private sealed class NeverPredicate : IHostPredicate
    {
        public bool IsMatch(RouteTarget target) => false;
    }

    private sealed class ProtocolPredicate : IHostPredicate
    {
        private readonly bool _isUdp;
        public ProtocolPredicate(bool isUdp) => _isUdp = isUdp;
        public bool IsMatch(RouteTarget target) => target.IsUdp == _isUdp;
    }

    private sealed class PortPredicate : IHostPredicate
    {
        private readonly int _from;
        private readonly int _to;
        public PortPredicate(int from, int to) { _from = from; _to = to; }
        public bool IsMatch(RouteTarget target) => target.Port >= _from && target.Port <= _to;
    }

    private sealed class CidrPredicate : IHostPredicate
    {
        private readonly AddressFamily _family;
        private readonly byte[] _network;
        private readonly int _prefix;

        public CidrPredicate(AddressFamily family, byte[] network, int prefix)
        {
            _family = family;
            _network = network;
            _prefix = prefix;
        }

        public bool IsMatch(RouteTarget target)
        {
            IPAddress address = target.Address;
            if (address.AddressFamily != _family) return false;

            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != _network.Length) return false;

            int fullBytes = _prefix / 8;
            for (int i = 0; i < fullBytes; i++)
            {
                if (bytes[i] != _network[i]) return false;
            }

            int remainingBits = _prefix % 8;
            if (remainingBits == 0) return true;
            int mask = 0xFF << (8 - remainingBits);
            return (bytes[fullBytes] & mask) == _network[fullBytes];
        }
    }

    private sealed class RegexPredicate : IHostPredicate
    {
        private readonly Regex _regex;
        public RegexPredicate(Regex regex) => _regex = regex;
        public bool IsMatch(RouteTarget target) => target.Host != null && _regex.IsMatch(target.Host);
    }

    private sealed class NamePredicate : IHostPredicate
    {
        private readonly HostMatcherType _matcher;
        private readonly string _pattern;

        public NamePredicate(HostMatcherType matcher, string pattern)
        {
            _matcher = matcher;
            _pattern = pattern;
        }

        public bool IsMatch(RouteTarget target)
        {
            string? host = target.Host;
            if (string.IsNullOrEmpty(host)) return false;

            switch (_matcher)
            {
                case HostMatcherType.Equals:
                    return string.Equals(host, _pattern, StringComparison.OrdinalIgnoreCase);

                case HostMatcherType.StartsWith:
                    return host!.StartsWith(_pattern, StringComparison.OrdinalIgnoreCase);

                case HostMatcherType.EndsWith:
                    return host!.EndsWith(_pattern, StringComparison.OrdinalIgnoreCase);

                case HostMatcherType.Contains:
                    return host!.IndexOf(_pattern, StringComparison.OrdinalIgnoreCase) >= 0;

                case HostMatcherType.DomainSuffix:
                    // "example.com" matches "example.com" and "www.example.com", but NOT
                    // "notexample.com" — the boundary must fall on a label separator.
                    if (string.Equals(host, _pattern, StringComparison.OrdinalIgnoreCase)) return true;
                    return host!.Length > _pattern.Length
                           && host[host.Length - _pattern.Length - 1] == '.'
                           && host.EndsWith(_pattern, StringComparison.OrdinalIgnoreCase);

                default:
                    return false;
            }
        }
    }
}
