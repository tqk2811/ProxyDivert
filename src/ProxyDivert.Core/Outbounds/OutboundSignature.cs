using System;
using System.Globalization;
using System.IO;
using System.Text;
using ProxyDivert.Core.Outbounds.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Outbounds;

// Everything about an outbound that its live IProxySource was built from, as one comparable string.
//
// It exists to answer one question: has this outbound changed enough that the running instance is
// wrong? Saving the configuration used to throw every source away, which for a VPN meant killing
// wireproxy and re-handshaking the tunnel each time the user ticked a checkbox somewhere else in
// the window. Comparing signatures instead means only the outbounds the user actually edited are
// rebuilt, and an untouched VPN keeps running.
//
// Deliberately NOT included: Name, which is a label the user reads, and Id, which is the key this
// signature is stored under.
public static class OutboundSignature
{
    /// <param name="wireProxyPath">
    /// The machine-wide wireproxy path. It is not part of the outbound, but a VPN source is built
    /// from it, so changing it has to invalidate every VPN instance.
    /// </param>
    public static string Of(Outbound outbound, string? wireProxyPath = null)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));

        var sb = new StringBuilder();
        sb.Append((int)outbound.Kind).Append('|')
          .Append(outbound.Url).Append('|')
          .Append(outbound.Username).Append('|')
          .Append(outbound.Password).Append('|')
          .Append(outbound.IsEnabled ? '1' : '0').Append('|')
          .Append((int)outbound.Ipv6Support);

        // A VPN's real settings often live in the file the outbound merely points at, so the path
        // alone would not notice the user editing it. Stamping the file makes "I changed my
        // WireGuard keys" reconnect the tunnel, which is what the user expects to happen. The
        // protocol and the group key belong here for the plainer reason that changing either one
        // means a different tunnel entirely.
        if (outbound.Kind == OutboundKind.Vpn)
        {
            sb.Append('|').Append((int)outbound.VpnProtocol)
              .Append('|').Append(outbound.PreSharedKey)
              .Append('|').Append(wireProxyPath)
              .Append('|').Append(StampFile(outbound.Address));
        }

        // The same reasoning for the key an SSH session logs in with: replacing the key file under
        // the same name is a different login. The host key the session trusted is deliberately NOT
        // here — it is recorded by the known-hosts store the first time the server is seen, and a
        // signature that moved when that happened would drop the very session that recorded it.
        if (outbound.Kind == OutboundKind.Ssh)
        {
            string? keyPath = string.IsNullOrWhiteSpace(outbound.PrivateKeyPath)
                ? null
                : OutboundAddress.Expand(outbound.PrivateKeyPath);
            sb.Append('|').Append(keyPath)
              .Append('|').Append(keyPath is null ? "-" : StampFile(keyPath));
        }

        return sb.ToString();
    }

    // Only the outbounds whose box actually names a file have one to stamp. It used to be handed
    // the raw box for every VPN, which meant an address-based one — sstp://vpn.example.com — was
    // asked for the last write time of a "file" by that name on every save, and answered "?".
    private static string StampFile(OutboundAddress? address)
        => address is null || !address.IsFile ? "-" : StampFile(address.Path!);

    private static string StampFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "-";
            return info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                + ":" + info.Length.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            // An unreadable path is a signature of its own; the failure surfaces when the source is
            // built, with a message that says what is wrong, rather than here.
            return "?";
        }
    }
}
