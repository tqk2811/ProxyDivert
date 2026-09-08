using System;
using System.Collections.Generic;
using System.Text;
using TqkLibrary.WinDivert.Inspection;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The SNI parser decides which domain rule a TLS connection matches, so it is worth exercising
// against a hand-built ClientHello rather than trusting a live capture.
public class TlsClientHelloParserTests
{
    // The parsers are ordinary objects now, so a test holds its own instead of reaching for statics.
    private static readonly TlsClientHelloParser Tls = new TlsClientHelloParser();
    private static readonly HttpHostParser Http = new HttpHostParser();

    [Fact]
    public void Reads_the_server_name_from_a_client_hello()
    {
        byte[] hello = BuildClientHello("www.example.com");

        Assert.True(Tls.TryReadHostName(hello, hello.Length, out string name));
        Assert.Equal("www.example.com", name);
    }

    [Fact]
    public void Reads_the_server_name_when_other_extensions_come_first()
    {
        byte[] hello = BuildClientHello("cdn.example.org", extensionsBefore: 3);

        Assert.True(Tls.TryReadHostName(hello, hello.Length, out string name));
        Assert.Equal("cdn.example.org", name);
    }

    [Fact]
    public void Returns_false_for_a_client_hello_without_sni()
    {
        byte[] hello = BuildClientHello(serverName: null);

        Assert.False(Tls.TryReadHostName(hello, hello.Length, out _));
    }

    [Fact]
    public void Returns_false_for_plain_http()
    {
        byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");

        Assert.False(Tls.CanParse(request, request.Length));
        Assert.False(Tls.TryReadHostName(request, request.Length, out _));
    }

    [Fact]
    public void Truncated_hello_is_rejected_instead_of_throwing()
    {
        byte[] hello = BuildClientHello("www.example.com");

        // Every prefix of a valid hello must be handled: a real first flight can be split.
        for (int length = 1; length < hello.Length; length++)
            Tls.TryReadHostName(hello, length, out _);
    }

    [Fact]
    public void Garbage_is_rejected_instead_of_throwing()
    {
        var random = new Random(1);
        byte[] buffer = new byte[512];
        for (int i = 0; i < 200; i++)
        {
            random.NextBytes(buffer);
            buffer[0] = 0x16;   // force the TLS pre-check to pass so the parser body runs
            buffer[1] = 0x03;
            Tls.TryReadHostName(buffer, buffer.Length, out _);
        }
    }

    // The shape that broke this: Chrome and Edge offering X25519MLKEM768 send a ClientHello of
    // 2.0-2.3KB whose ~1.2KB key_share sits BEFORE server_name, so the SNI lands past the 2048
    // bytes the parser used to be given. Nothing reported the miss — routing by domain just
    // quietly became routing by reverse DNS, or by raw address.
    [Fact]
    public void Reads_the_server_name_of_a_post_quantum_sized_hello()
    {
        byte[] hello = BuildClientHello("www.example.com", bytesBeforeSni: 2000);

        Assert.True(hello.Length > 2048, "the test hello is meant to be bigger than the old ceiling");
        Assert.True(Tls.RecommendedPeekSize >= hello.Length, "the parser asks for fewer bytes than such a hello needs");
        Assert.True(Tls.TryReadHostName(hello, hello.Length, out string name));
        Assert.Equal("www.example.com", name);
    }

    // Telling "the client has not finished" from "the client finished and named nothing" is what
    // keeps the inspector from waiting out its whole peek timeout on the latter.
    [Fact]
    public void A_hello_that_has_all_arrived_is_not_waited_on_any_longer()
    {
        byte[] hello = BuildClientHello(serverName: null);

        Assert.False(Tls.WantsMoreData(hello, hello.Length));
    }

    [Fact]
    public void A_hello_still_arriving_is_waited_on()
    {
        byte[] hello = BuildClientHello("www.example.com", bytesBeforeSni: 2000);

        Assert.True(Tls.WantsMoreData(hello, 100));
        Assert.True(Tls.WantsMoreData(hello, hello.Length - 1));
    }

    [Fact]
    public void An_http_request_is_waited_on_only_until_its_headers_end()
    {
        byte[] partial = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nUser-Agent: x\r\n");
        byte[] complete = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nUser-Agent: x\r\n\r\n");

        Assert.True(Http.WantsMoreData(partial, partial.Length));
        Assert.False(Http.WantsMoreData(complete, complete.Length));
    }

    // Builds a minimal but structurally valid TLS 1.2 ClientHello.
    private static byte[] BuildClientHello(string? serverName, int extensionsBefore = 0, int bytesBeforeSni = 0)
    {
        var extensions = new List<byte>();

        // Filler extensions to prove the parser walks the list rather than assuming SNI is first.
        for (int i = 0; i < extensionsBefore; i++)
        {
            extensions.AddRange(new byte[] { 0x00, (byte)(0x0A + i) });  // type
            extensions.AddRange(new byte[] { 0x00, 0x02 });              // length
            extensions.AddRange(new byte[] { 0x00, 0x00 });              // body
        }

        // One big extension ahead of the SNI, standing in for a post-quantum key_share.
        if (bytesBeforeSni > 0)
        {
            extensions.AddRange(new byte[] { 0x00, 0x33 });              // key_share
            extensions.AddRange(BE16(bytesBeforeSni));
            extensions.AddRange(new byte[bytesBeforeSni]);
        }

        if (serverName != null)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(serverName);
            var entry = new List<byte> { 0x00 };                          // name_type = host_name
            entry.AddRange(BE16(nameBytes.Length));
            entry.AddRange(nameBytes);

            var list = new List<byte>();
            list.AddRange(BE16(entry.Count));                             // server_name_list length
            list.AddRange(entry);

            extensions.AddRange(new byte[] { 0x00, 0x00 });               // extension type = SNI
            extensions.AddRange(BE16(list.Count));
            extensions.AddRange(list);
        }

        var body = new List<byte>();
        body.AddRange(new byte[] { 0x03, 0x03 });                         // client_version TLS 1.2
        body.AddRange(new byte[32]);                                      // random
        body.Add(0x00);                                                   // session id length
        body.AddRange(BE16(2));                                           // cipher suites length
        body.AddRange(new byte[] { 0x13, 0x01 });
        body.Add(0x01);                                                   // compression methods length
        body.Add(0x00);
        body.AddRange(BE16(extensions.Count));
        body.AddRange(extensions);

        var handshake = new List<byte> { 0x01 };                          // ClientHello
        handshake.AddRange(BE24(body.Count));
        handshake.AddRange(body);

        var record = new List<byte> { 0x16, 0x03, 0x01 };                 // handshake, TLS 1.0 record
        record.AddRange(BE16(handshake.Count));
        record.AddRange(handshake);
        return record.ToArray();
    }

    private static byte[] BE16(int value) => new[] { (byte)(value >> 8), (byte)value };

    private static byte[] BE24(int value) => new[] { (byte)(value >> 16), (byte)(value >> 8), (byte)value };
}
