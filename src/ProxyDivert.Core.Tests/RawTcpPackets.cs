using System;
using System.Net;
using System.Net.Sockets;

namespace ProxyDivert.Core.Tests;

// Hand-built IPv4/IPv6 + TCP packets for the tests that exercise the packet path without a
// driver. Checksums are left at zero — nothing under test computes them, the driver does.
internal static class RawTcpPackets
{
    public const byte Fin = 0x01;
    public const byte Syn = 0x02;
    public const byte Rst = 0x04;
    public const byte Psh = 0x08;
    public const byte Ack = 0x10;

    public static byte[] Build(
        IPAddress source, ushort sourcePort, IPAddress destination, ushort destinationPort,
        uint sequence, uint acknowledgment, byte flags, int payloadLength)
    {
        bool isIpv6 = source.AddressFamily == AddressFamily.InterNetworkV6;
        int ipHeaderLength = isIpv6 ? 40 : 20;
        const int tcpHeaderLength = 20;
        byte[] b = new byte[ipHeaderLength + tcpHeaderLength + payloadLength];

        if (isIpv6)
        {
            b[0] = 0x60;
            WriteUInt16(b, 4, (ushort)(tcpHeaderLength + payloadLength));
            b[6] = 6;
            b[7] = 64;
            Buffer.BlockCopy(source.GetAddressBytes(), 0, b, 8, 16);
            Buffer.BlockCopy(destination.GetAddressBytes(), 0, b, 24, 16);
        }
        else
        {
            b[0] = 0x45;
            WriteUInt16(b, 2, (ushort)b.Length);
            b[8] = 64;
            b[9] = 6;
            Buffer.BlockCopy(source.GetAddressBytes(), 0, b, 12, 4);
            Buffer.BlockCopy(destination.GetAddressBytes(), 0, b, 16, 4);
        }

        int t = ipHeaderLength;
        WriteUInt16(b, t, sourcePort);
        WriteUInt16(b, t + 2, destinationPort);
        WriteUInt32(b, t + 4, sequence);
        WriteUInt32(b, t + 8, acknowledgment);
        b[t + 12] = 0x50;                 // data offset 5 words
        b[t + 13] = flags;
        WriteUInt16(b, t + 14, 0xFFFF);   // window
        for (int i = 0; i < payloadLength; i++) b[t + tcpHeaderLength + i] = (byte)(0x41 + (i % 26));
        return b;
    }

    private static void WriteUInt16(byte[] b, int at, ushort value)
    {
        b[at] = (byte)(value >> 8);
        b[at + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] b, int at, uint value)
    {
        b[at] = (byte)(value >> 24);
        b[at + 1] = (byte)(value >> 16);
        b[at + 2] = (byte)(value >> 8);
        b[at + 3] = (byte)value;
    }
}
