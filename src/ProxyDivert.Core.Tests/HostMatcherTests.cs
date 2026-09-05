using System.Net;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using Xunit;

namespace ProxyDivert.Core.Tests;

public class HostMatcherTests
{
    private static readonly IPAddress SomeIp = IPAddress.Parse("93.184.216.34");

    [Theory]
    [InlineData("*.example.com", "www.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]      // no label to fill the star
    [InlineData("*example.com", "example.com", true)]
    [InlineData("cdn?.example.com", "cdn1.example.com", true)]
    [InlineData("cdn?.example.com", "cdn12.example.com", false)]
    [InlineData("*.EXAMPLE.com", "www.example.com", true)]   // case-insensitive
    public void Wildcard_matches_as_expected(string pattern, string host, bool expected)
        => Assert.Equal(expected, HostMatcher.IsMatch(HostMatcherType.Wildcard, pattern, host, SomeIp, 443));

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "www.example.com", true)]
    [InlineData("example.com", "notexample.com", false)]     // the boundary must be a dot
    [InlineData("example.com", "example.com.evil.net", false)]
    public void DomainSuffix_respects_label_boundary(string pattern, string host, bool expected)
        => Assert.Equal(expected, HostMatcher.IsMatch(HostMatcherType.DomainSuffix, pattern, host, SomeIp, 443));

    [Fact]
    public void Name_matchers_never_match_when_the_connection_has_no_name()
    {
        foreach (HostMatcherType matcher in new[]
                 {
                     HostMatcherType.Wildcard, HostMatcherType.Equals, HostMatcherType.DomainSuffix,
                     HostMatcherType.StartsWith, HostMatcherType.EndsWith, HostMatcherType.Contains,
                     HostMatcherType.Regex,
                 })
        {
            Assert.False(HostMatcher.IsMatch(matcher, "*", host: null, SomeIp, 443), matcher.ToString());
        }
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.1.2.3", true)]
    [InlineData("10.0.0.0/8", "11.1.2.3", false)]
    [InlineData("192.168.1.0/24", "192.168.1.255", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("192.168.1.128/25", "192.168.1.200", true)]  // partial-byte prefix
    [InlineData("192.168.1.128/25", "192.168.1.100", false)]
    [InlineData("1.2.3.4", "1.2.3.4", true)]                 // no prefix = exact address
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    public void IpCidr_matches_ipv4(string pattern, string address, bool expected)
        => Assert.Equal(expected, HostMatcher.IsMatch(HostMatcherType.IpCidr, pattern, null, IPAddress.Parse(address), 443));

    [Theory]
    [InlineData("udp", true, true)]
    [InlineData("udp", false, false)]
    [InlineData("tcp", false, true)]
    [InlineData("tcp", true, false)]
    [InlineData("UDP", true, true)]                          // case-insensitive
    [InlineData("  tcp  ", false, true)]                     // trimmed like every other pattern
    public void Protocol_matches_the_transport(string pattern, bool isUdp, bool expected)
        => Assert.Equal(expected, HostMatcher.IsMatch(HostMatcherType.Protocol, pattern, null, SomeIp, 53, isUdp));

    [Theory]
    [InlineData("both")]
    [InlineData("icmp")]
    [InlineData("*")]
    public void Protocol_never_matches_a_pattern_that_is_not_tcp_or_udp(string pattern)
    {
        // A typo must claim nothing rather than everything — the wrong half of that choice would
        // silently route traffic the rule was written to leave alone.
        Assert.False(HostMatcher.IsMatch(HostMatcherType.Protocol, pattern, null, SomeIp, 53, isUdp: true));
        Assert.False(HostMatcher.IsMatch(HostMatcherType.Protocol, pattern, null, SomeIp, 53, isUdp: false));
    }

    [Fact]
    public void Protocol_matches_without_a_host_name()
    {
        // The whole point: DNS has no name to match on, and this is the matcher that still claims it.
        Assert.True(HostMatcher.IsMatch(HostMatcherType.Protocol, "udp", host: null, SomeIp, 53, isUdp: true));
    }

    [Fact]
    public void IpCidr_never_matches_across_address_families()
        => Assert.False(HostMatcher.IsMatch(HostMatcherType.IpCidr, "10.0.0.0/8", null, IPAddress.Parse("::1"), 443));

    [Theory]
    [InlineData("443", 443, true)]
    [InlineData("443", 80, false)]
    [InlineData("8000-8100", 8080, true)]
    [InlineData("8000-8100", 8101, false)]
    [InlineData("8100-8000", 8080, true)]                    // reversed range still works
    public void Port_matches_single_and_range(string pattern, int port, bool expected)
        => Assert.Equal(expected, HostMatcher.IsMatch(HostMatcherType.Port, pattern, null, SomeIp, port));

    [Fact]
    public void Invalid_regex_never_matches_and_never_throws()
        => Assert.False(HostMatcher.IsMatch(HostMatcherType.Regex, "([unclosed", "anything", SomeIp, 443));

    [Fact]
    public void Empty_pattern_never_matches()
        => Assert.False(HostMatcher.IsMatch(HostMatcherType.Equals, "   ", "example.com", SomeIp, 443));
}
