using System;
using System.Net;
using TqkLibrary.WinDivert.Packet;
using TqkLibrary.WinDivert.Packet.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The reset that closes a pre-existing connection from the process's side. Byte-level, because
// the stack is unforgiving: a reset whose sequence number is not exactly the next expected byte is
// answered with a challenge ACK and the connection lives on.
public class TcpResetPacketBuilderTests
{
    private static readonly IPAddress Client4 = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Server4 = IPAddress.Parse("93.184.216.34");
    private static readonly IPAddress Client6 = IPAddress.Parse("2001:db8::5");
    private static readonly IPAddress Server6 = IPAddress.Parse("2606:4700::1111");

    private static ParsedPacket Parse(byte[] packet)
        => PacketParser.Default.TryParse(packet, packet.Length) ?? throw new InvalidOperationException("unparseable");

    [Fact]
    public void The_reset_travels_back_to_the_sender_carrying_the_byte_it_expects_next()
    {
        byte[] sent = RawTcpPackets.Build(Client4, 51000, Server4, 443,
            sequence: 1000, acknowledgment: 5000, flags: RawTcpPackets.Ack | RawTcpPackets.Psh, payloadLength: 10);

        byte[] reset = new TcpResetPacketBuilder().BuildResetTowardsSender(Parse(sent));
        ParsedPacket parsed = Parse(reset);

        Assert.Equal(40, reset.Length);
        Assert.False(parsed.IsIpv6);
        Assert.True(parsed.IsTcp);
        Assert.Equal(Server4, parsed.Source);
        Assert.Equal(Client4, parsed.Destination);
        Assert.Equal(443, parsed.SourcePort);
        Assert.Equal(51000, parsed.DestinationPort);
        Assert.True(parsed.Tcp.Rst);
        Assert.True(parsed.Tcp.Ack);
        Assert.False(parsed.Tcp.Syn);
        Assert.Equal(5000u, parsed.Tcp.SequenceNumber);
        Assert.Equal(1010u, parsed.Tcp.AcknowledgmentNumber);
        Assert.Equal(0, parsed.PayloadLength);
        Assert.Equal(20, parsed.Tcp.DataOffset);
    }

    [Fact]
    public void Checksums_are_left_for_the_injector()
    {
        byte[] sent = RawTcpPackets.Build(Client4, 51000, Server4, 443, 1, 1, RawTcpPackets.Ack, 0);

        byte[] reset = new TcpResetPacketBuilder().BuildResetTowardsSender(Parse(sent));

        Assert.Equal(0, reset[10] | reset[11]);   // IPv4 header checksum
        Assert.Equal(0, reset[36] | reset[37]);   // TCP checksum
    }

    [Fact]
    public void Ipv6_gets_the_fixed_header_and_the_same_tcp_segment()
    {
        byte[] sent = RawTcpPackets.Build(Client6, 51000, Server6, 443,
            sequence: 700, acknowledgment: 9000, flags: RawTcpPackets.Ack, payloadLength: 3);

        byte[] reset = new TcpResetPacketBuilder().BuildResetTowardsSender(Parse(sent));
        ParsedPacket parsed = Parse(reset);

        Assert.Equal(60, reset.Length);
        Assert.True(parsed.IsIpv6);
        Assert.Equal(20, parsed.Ipv6.PayloadLength);
        Assert.Equal(6, parsed.Ipv6.NextHeader);
        Assert.Equal(Server6, parsed.Source);
        Assert.Equal(Client6, parsed.Destination);
        Assert.True(parsed.Tcp.Rst);
        Assert.Equal(9000u, parsed.Tcp.SequenceNumber);
        Assert.Equal(703u, parsed.Tcp.AcknowledgmentNumber);
    }

    // SYN and FIN each occupy a byte of sequence space, and the peer would have counted them.
    [Fact]
    public void A_fin_is_acknowledged_as_one_byte()
    {
        byte[] sent = RawTcpPackets.Build(Client4, 51000, Server4, 443,
            sequence: 77, acknowledgment: 1, flags: RawTcpPackets.Ack | RawTcpPackets.Fin, payloadLength: 0);

        ParsedPacket parsed = Parse(new TcpResetPacketBuilder().BuildResetTowardsSender(Parse(sent)));

        Assert.Equal(78u, parsed.Tcp.AcknowledgmentNumber);
    }

    [Fact]
    public void Only_tcp_can_be_reset()
    {
        byte[] udp = RawTcpPackets.Build(Client4, 51000, Server4, 443, 1, 1, RawTcpPackets.Ack, 0);
        udp[9] = 17;   // protocol = UDP

        Assert.Throws<ArgumentException>(() => new TcpResetPacketBuilder().BuildResetTowardsSender(Parse(udp)));
    }
}
