using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TqkLibrary.VpnClient.Sockets;
using TqkLibrary.VpnClient.Tunnels;

namespace ProxyDivert.Core.Vpn.Client;

// Resolves host names INSIDE the tunnel, over the tunnel's own UDP socket.
//
// This exists because the obvious alternative is a leak. Asking Dns.GetHostAddresses would send the
// name the user is visiting to whatever resolver the machine uses — their ISP, usually — over the
// ordinary network, seconds before the traffic itself goes through the VPN. The packets would be
// private and the list of who they were talking to would not, which is the one thing a VPN outbound
// is for. So the query rides the tunnel like everything else, to the DNS server the VPN handed out.
//
// It is a small resolver on purpose: A and AAAA over UDP, one retry, no EDNS, no DNSSEC. Anything a
// proxied connection needs is a name to dial; the elaborate cases belong to a real resolver, and the
// DoH path elsewhere in the tool already covers the target process's own lookups.
internal sealed class InTunnelResolver : IDisposable
{
    // Where to ask when the VPN handed out no DNS server of its own. Still inside the tunnel, so it
    // is not a leak — just a resolver that is reachable from nearly every exit.
    private static readonly IPAddress[] Fallbacks =
    {
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8"),
    };

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    // Long enough that a page's worth of connections asks once, short enough that a name which moved
    // is picked up in a minute. The TTL from the answer caps it, so a deliberately short record
    // stays short.
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromMinutes(5);

    private readonly VpnTunnel _tunnel;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public InTunnelResolver(VpnTunnel tunnel, ILogger? logger = null)
    {
        _tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
        _logger = logger;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> to an address the tunnel can dial. An IP literal is returned
    /// as it stands. IPv4 is preferred; AAAA is only asked for when the tunnel actually carries a
    /// global IPv6, because an address it cannot route is worse than no answer.
    /// </summary>
    public async Task<IPAddress> ResolveAsync(string host, bool allowIpv6, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is empty.", nameof(host));

        string name = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(name, out IPAddress? literal)) return literal;

        if (_cache.TryGetValue(name, out CacheEntry? cached) && !cached.IsStale)
            return cached.Address;

        IPAddress? answer = await QueryAsync(name, DnsType.A, cancellationToken).ConfigureAwait(false);
        if (answer is null && allowIpv6)
            answer = await QueryAsync(name, DnsType.Aaaa, cancellationToken).ConfigureAwait(false);

        if (answer is null)
            throw new SocketException((int)SocketError.HostNotFound);

        return answer;
    }

    private async Task<IPAddress?> QueryAsync(string name, ushort type, CancellationToken cancellationToken)
    {
        byte[] question = BuildQuery(name, type, out ushort id);

        foreach (IPAddress server in Servers())
        {
            // Two goes at the same server: a UDP query lost on a freshly established tunnel is
            // common enough that giving up on it would look like a broken VPN.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    byte[]? reply = await AskAsync(server, question, cancellationToken).ConfigureAwait(false);
                    if (reply is null) continue;

                    (IPAddress? address, uint ttl) = ReadAnswer(reply, id, type);
                    if (address is null) return null;

                    _cache[name] = new CacheEntry(address, Expiry(ttl));
                    return address;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "in-tunnel dns query for {Name} via {Server} failed", name, server);
                }
            }
        }

        return null;
    }

    private async Task<byte[]?> AskAsync(IPAddress server, byte[] question, CancellationToken cancellationToken)
    {
        VpnUdpClient socket = VpnUdpClient.Connect(_tunnel.Stack, server, 53);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            socket.Send(question);
            return await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;   // the query timed out; the caller decides whether to try again
        }
        finally
        {
            _tunnel.Stack.UnbindUdp(socket.LocalPort);
        }
    }

    private IEnumerable<IPAddress> Servers()
    {
        if (_tunnel.AssignedDns is IPAddress assigned) yield return assigned;
        foreach (IPAddress fallback in Fallbacks) yield return fallback;
    }

    // --- wire format ---------------------------------------------------------------------------

    private byte[] BuildQuery(string name, ushort type, out ushort id)
    {
        id = unchecked((ushort)Interlocked.Increment(ref _nextId));

        var buffer = new List<byte>(name.Length + 32);
        buffer.Add((byte)(id >> 8));
        buffer.Add((byte)id);
        buffer.Add(0x01);            // recursion desired
        buffer.Add(0x00);
        buffer.Add(0x00); buffer.Add(0x01);   // one question
        buffer.Add(0x00); buffer.Add(0x00);   // no answers
        buffer.Add(0x00); buffer.Add(0x00);   // no authority
        buffer.Add(0x00); buffer.Add(0x00);   // no additional

        foreach (string label in name.Split('.'))
        {
            if (label.Length == 0) continue;
            if (label.Length > 63) throw new FormatException($"'{name}' has a label longer than 63 bytes.");
            buffer.Add((byte)label.Length);
            foreach (char c in label) buffer.Add((byte)c);
        }
        buffer.Add(0x00);            // root label ends the name

        buffer.Add((byte)(type >> 8)); buffer.Add((byte)type);
        buffer.Add(0x00); buffer.Add(0x01);   // class IN

        return buffer.ToArray();
    }

    // Walks the answer section for the first address record of the type asked for. A CNAME chain
    // needs no following: a resolver that answers one puts the addresses in the same reply.
    private static (IPAddress? Address, uint Ttl) ReadAnswer(byte[] reply, ushort id, ushort type)
    {
        if (reply.Length < 12) return (null, 0);
        if (((reply[0] << 8) | reply[1]) != id) return (null, 0);   // not the reply to our question
        if ((reply[3] & 0x0F) != 0) return (null, 0);               // the server said no

        int questions = (reply[4] << 8) | reply[5];
        int answers = (reply[6] << 8) | reply[7];
        int offset = 12;

        for (int i = 0; i < questions; i++)
        {
            if (!SkipName(reply, ref offset)) return (null, 0);
            offset += 4;                                            // qtype + qclass
        }

        for (int i = 0; i < answers; i++)
        {
            if (!SkipName(reply, ref offset)) return (null, 0);
            if (offset + 10 > reply.Length) return (null, 0);

            int recordType = (reply[offset] << 8) | reply[offset + 1];
            uint ttl = (uint)((reply[offset + 4] << 24) | (reply[offset + 5] << 16)
                              | (reply[offset + 6] << 8) | reply[offset + 7]);
            int length = (reply[offset + 8] << 8) | reply[offset + 9];
            offset += 10;

            if (offset + length > reply.Length) return (null, 0);

            if (recordType == type && (length == 4 || length == 16))
            {
                var bytes = new byte[length];
                Buffer.BlockCopy(reply, offset, bytes, 0, length);
                return (new IPAddress(bytes), ttl);
            }

            offset += length;
        }

        return (null, 0);
    }

    // Names are either labels ending in a zero byte or a two-byte pointer into the message; either
    // way this only has to get past one.
    private static bool SkipName(byte[] message, ref int offset)
    {
        while (offset < message.Length)
        {
            byte length = message[offset];
            if (length == 0) { offset++; return true; }
            if ((length & 0xC0) == 0xC0) { offset += 2; return offset <= message.Length; }
            offset += length + 1;
        }
        return false;
    }

    private static DateTime Expiry(uint ttl)
    {
        TimeSpan lifetime = ttl == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(ttl);
        if (lifetime > MaxCacheAge) lifetime = MaxCacheAge;
        return DateTime.UtcNow + lifetime;
    }

    public void Dispose() => _cache.Clear();

    private static class DnsType
    {
        public const ushort A = 1;
        public const ushort Aaaa = 28;
    }

    private sealed class CacheEntry
    {
        public CacheEntry(IPAddress address, DateTime expiresUtc)
        {
            Address = address;
            ExpiresUtc = expiresUtc;
        }

        public IPAddress Address { get; }

        public DateTime ExpiresUtc { get; }

        public bool IsStale => DateTime.UtcNow >= ExpiresUtc;
    }
}
