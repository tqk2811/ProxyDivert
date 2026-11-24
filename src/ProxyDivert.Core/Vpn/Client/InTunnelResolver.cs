using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Vpn.Exceptions;
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

        string cacheKey = CacheKey(name, allowIpv6);
        if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && !cached.IsStale)
            return cached.Address;

        IPAddress? answer = await QueryAsync(name, cacheKey, DnsType.A, cancellationToken).ConfigureAwait(false);
        if (answer is null && allowIpv6)
            answer = await QueryAsync(name, cacheKey, DnsType.Aaaa, cancellationToken).ConfigureAwait(false);

        if (answer is null)
            throw new SocketException((int)SocketError.HostNotFound);

        return answer;
    }

    // An answer found with AAAA allowed may be an IPv6 address, which a caller that cannot route IPv6
    // must never be handed from the cache.
    internal static string CacheKey(string name, bool allowIpv6) => (allowIpv6 ? "6|" : "4|") + name;

    private async Task<IPAddress?> QueryAsync(string name, string cacheKey, ushort type, CancellationToken cancellationToken)
    {
        byte[] question = BuildQuery(name, type, out ushort id);

        foreach ((IPAddress server, bool isTheVpnsOwn) in Servers())
        {
            bool answered = false;

            // Two goes at the same server: a UDP query lost on a freshly established tunnel is
            // common enough that giving up on it would look like a broken VPN.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    byte[]? reply = await AskAsync(server, question, cancellationToken).ConfigureAwait(false);
                    if (reply is null) continue;

                    answered = true;
                    DnsAnswer parsed = ReadAnswer(reply, id, type);
                    if (parsed.Address is null)
                    {
                        if (parsed.Final) return null;
                        break;   // not a verdict on the name: ask the next server instead of this one again
                    }

                    _cache[cacheKey] = new CacheEntry(parsed.Address, Expiry(parsed.Ttl));
                    return parsed.Address;
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

            // Silence from the resolver the VPN itself handed out is a verdict on the TUNNEL, not
            // on the name: that address is reachable from inside any tunnel that works at all.
            // Walking on to 1.1.1.1 and 8.8.8.8 sends two more doomed queries down the same dead
            // tunnel and turns a ten-second failure into a thirty-second one, which is what the
            // user actually waits through on every request after a session dies.
            if (isTheVpnsOwn && !answered)
                throw new TunnelNotCarryingTrafficException(server, QueryTimeout + QueryTimeout);
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

    // The flag marks the one server whose silence means something: the resolver the VPN assigned.
    private IEnumerable<(IPAddress Server, bool IsTheVpnsOwn)> Servers()
    {
        if (_tunnel.AssignedDns is IPAddress assigned) yield return (assigned, true);
        foreach (IPAddress fallback in Fallbacks) yield return (fallback, false);
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
    internal static DnsAnswer ReadAnswer(byte[] reply, ushort id, ushort type)
    {
        if (reply.Length < 12) return new DnsAnswer(null, 0, final: false);
        if (((reply[0] << 8) | reply[1]) != id) return new DnsAnswer(null, 0, final: false);   // not the reply to our question
        int rcode = reply[3] & 0x0F;
        // NXDOMAIN is the zone speaking: the name does not exist, and another server will say the same.
        // Any other refusal (SERVFAIL, REFUSED, ...) is this server having a bad moment.
        if (rcode == 3) return new DnsAnswer(null, 0, final: true);
        if (rcode != 0) return new DnsAnswer(null, 0, final: false);

        int questions = (reply[4] << 8) | reply[5];
        int answers = (reply[6] << 8) | reply[7];
        int offset = 12;

        for (int i = 0; i < questions; i++)
        {
            if (!SkipName(reply, ref offset)) return new DnsAnswer(null, 0, final: false);
            offset += 4;                                            // qtype + qclass
        }

        for (int i = 0; i < answers; i++)
        {
            if (!SkipName(reply, ref offset)) return new DnsAnswer(null, 0, final: false);
            if (offset + 10 > reply.Length) return new DnsAnswer(null, 0, final: false);

            int recordType = (reply[offset] << 8) | reply[offset + 1];
            uint ttl = (uint)((reply[offset + 4] << 24) | (reply[offset + 5] << 16)
                              | (reply[offset + 6] << 8) | reply[offset + 7]);
            int length = (reply[offset + 8] << 8) | reply[offset + 9];
            offset += 10;

            if (offset + length > reply.Length) return new DnsAnswer(null, 0, final: false);

            if (recordType == type && (length == 4 || length == 16))
            {
                var bytes = new byte[length];
                Buffer.BlockCopy(reply, offset, bytes, 0, length);
                return new DnsAnswer(new IPAddress(bytes), ttl, final: true);
            }

            offset += length;
        }

        // A well-formed, successful reply with no record of this type: that is the answer.
        return new DnsAnswer(null, 0, final: true);
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

    internal readonly struct DnsAnswer
    {
        public DnsAnswer(IPAddress? address, uint ttl, bool final)
        {
            Address = address;
            Ttl = ttl;
            Final = final;
        }

        public IPAddress? Address { get; }

        public uint Ttl { get; }

        /// <summary>True when no other server is worth asking: an address, NXDOMAIN, or an empty success.</summary>
        public bool Final { get; }
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
