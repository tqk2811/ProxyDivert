using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Vpn;

/// <summary>
/// The server names VPN outbounds configured from a file dial, read out of those files.
/// </summary>
/// <remarks>
/// Secure DNS must never take over the lookup of a tunnel's own server — resolving it through the
/// tunnel that needs it is a loop. An outbound with an address in its URL box names its server
/// there; one pointing at a .ovpn, .conf or .vpn file names it inside the file, and this is what
/// reads it. It touches the disk, so it is called where the routing table is built (Start, Save),
/// never on the packet path. A file that cannot be read contributes nothing: building that outbound
/// will fail on its own and say why.
/// </remarks>
public static class VpnServerHostReader
{
    public static IReadOnlyCollection<string> Read(IEnumerable<Outbound> outbounds, ILogger logger)
    {
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));
        if (logger is null) throw new ArgumentNullException(nameof(logger));

        var hosts = new List<string>();
        foreach (Outbound outbound in outbounds)
        {
            if (!outbound.IsEnabled || outbound.Kind != OutboundKind.Vpn) continue;
            if (outbound.Address is not { IsFile: true } address || string.IsNullOrEmpty(address.Path)) continue;

            try
            {
                if (address.Path!.EndsWith(".vpn", StringComparison.OrdinalIgnoreCase))
                {
                    string? host = VpnProfileReader.Read(outbound).Host;
                    if (!string.IsNullOrWhiteSpace(host)) hosts.Add(host!);
                }
                else
                {
                    hosts.AddRange(ParseConfigHosts(File.ReadAllLines(address.Path)));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "VPN outbound {Outbound}: cannot read the server name from {Path}: {Error}",
                    outbound.Name, address.Path, ex.Message);
            }
        }
        return hosts;
    }

    /// <summary>
    /// The server names of an OpenVPN profile (<c>remote host [port]</c>) or a WireGuard
    /// configuration (<c>Endpoint = host:port</c>). IP literals come back as they are; the routing
    /// table ignores them.
    /// </summary>
    internal static IEnumerable<string> ParseConfigHosts(IEnumerable<string> lines)
    {
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            if (line.StartsWith("remote ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("remote\t", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) yield return parts[1];
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line.Substring(0, eq).Trim().Equals("Endpoint", StringComparison.OrdinalIgnoreCase)) continue;

            string? host = HostOfEndpoint(line.Substring(eq + 1).Trim());
            if (host != null) yield return host;
        }
    }

    // "host:port", "[v6]:port", or a bare host.
    private static string? HostOfEndpoint(string endpoint)
    {
        if (endpoint.Length == 0) return null;
        if (endpoint[0] == '[')
        {
            int close = endpoint.IndexOf(']');
            return close > 1 ? endpoint.Substring(1, close - 1) : null;
        }
        int colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint.Substring(0, colon) : endpoint;
    }
}
