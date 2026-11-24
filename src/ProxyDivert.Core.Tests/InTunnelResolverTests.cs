using System.Net;
using ProxyDivert.Core.Vpn.Client;
using Xunit;

namespace ProxyDivert.Core.Tests;

public class InTunnelResolverTests
{
    private const ushort Id = 0x1234;
    private const ushort TypeA = 1;

    private static byte[] Header(int rcode, int answers = 0)
        => new byte[] { Id >> 8, Id & 0xFF, 0x81, (byte)(0x80 | rcode), 0, 0, 0, (byte)answers, 0, 0, 0, 0 };

    [Theory]
    [InlineData(2)]   // SERVFAIL
    [InlineData(5)]   // REFUSED
    public void A_server_failure_is_not_a_verdict_so_the_next_server_is_asked(int rcode)
    {
        InTunnelResolver.DnsAnswer answer = InTunnelResolver.ReadAnswer(Header(rcode), Id, TypeA);

        Assert.Null(answer.Address);
        Assert.False(answer.Final);
    }

    [Fact]
    public void NXDOMAIN_is_final()
    {
        InTunnelResolver.DnsAnswer answer = InTunnelResolver.ReadAnswer(Header(3), Id, TypeA);

        Assert.Null(answer.Address);
        Assert.True(answer.Final);
    }

    [Fact]
    public void An_address_record_is_the_answer()
    {
        byte[] header = Header(0, answers: 1);
        byte[] record =
        {
            0xC0, 0x0C,             // name: pointer
            0, 1, 0, 1,             // type A, class IN
            0, 0, 0, 60,            // ttl
            0, 4, 1, 2, 3, 4,       // rdata
        };
        byte[] reply = new byte[header.Length + record.Length];
        header.CopyTo(reply, 0);
        record.CopyTo(reply, header.Length);

        InTunnelResolver.DnsAnswer answer = InTunnelResolver.ReadAnswer(reply, Id, TypeA);

        Assert.Equal(IPAddress.Parse("1.2.3.4"), answer.Address);
        Assert.True(answer.Final);
    }

    [Fact]
    public void An_answer_found_with_ipv6_allowed_is_not_served_to_a_caller_that_cannot_route_ipv6()
        => Assert.NotEqual(InTunnelResolver.CacheKey("example.com", true), InTunnelResolver.CacheKey("example.com", false));
}
