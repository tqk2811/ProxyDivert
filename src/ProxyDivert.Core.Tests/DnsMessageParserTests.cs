using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using TqkLibrary.WinDivert.SecureDns;
using TqkLibrary.WinDivert.SecureDns.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

public class DnsMessageParserTests
{
    private static readonly DnsMessageParser Parser = new DnsMessageParser();

    [Fact]
    public void Parses_a_single_a_record()
    {
        byte[] response = BuildResponse("example.com", new[]
        {
            Answer("example.com", TypeA, IPAddress.Parse("93.184.216.34"), ttl: 300),
        });

        IReadOnlyList<DnsAddressRecord> records = Parser.ParseAddressAnswers(response, 0, response.Length);

        DnsAddressRecord record = Assert.Single(records);
        Assert.Equal("example.com", record.QuestionName);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), record.Address);
        Assert.Equal(300, record.Ttl.TotalSeconds);
    }

    [Fact]
    public void Attributes_addresses_behind_a_cname_to_the_queried_name()
    {
        // What a CDN actually returns: www.example.com -> cdn.provider.net -> 1.2.3.4
        byte[] response = BuildResponse("www.example.com", new[]
        {
            Cname("www.example.com", "cdn.provider.net", ttl: 60),
            Answer("cdn.provider.net", TypeA, IPAddress.Parse("1.2.3.4"), ttl: 60),
        });

        IReadOnlyList<DnsAddressRecord> records = Parser.ParseAddressAnswers(response, 0, response.Length);

        DnsAddressRecord record = Assert.Single(records);
        // A rule saying "*.example.com goes through the proxy" must still match here.
        Assert.Equal("www.example.com", record.QuestionName);
        Assert.Equal("cdn.provider.net", record.OwnerName);
    }

    [Fact]
    public void Parses_aaaa_records()
    {
        byte[] response = BuildResponse("example.com", new[]
        {
            Answer("example.com", TypeAaaa, IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), ttl: 120),
        });

        DnsAddressRecord record = Assert.Single(Parser.ParseAddressAnswers(response, 0, response.Length));
        Assert.Equal(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), record.Address);
    }

    [Fact]
    public void Ignores_a_query_packet()
    {
        byte[] query = BuildResponse("example.com", Array.Empty<AnswerSpec>(), isResponse: false);

        Assert.Empty(Parser.ParseAddressAnswers(query, 0, query.Length));
    }

    [Fact]
    public void Ignores_an_error_response()
    {
        byte[] response = BuildResponse("nope.example.com", new[]
        {
            Answer("nope.example.com", TypeA, IPAddress.Parse("1.1.1.1"), ttl: 60),
        }, rcode: 3);   // NXDOMAIN

        Assert.Empty(Parser.ParseAddressAnswers(response, 0, response.Length));
    }

    [Fact]
    public void Truncated_and_random_input_never_throws()
    {
        byte[] response = BuildResponse("example.com", new[]
        {
            Answer("example.com", TypeA, IPAddress.Parse("93.184.216.34"), ttl: 300),
        });
        for (int length = 1; length < response.Length; length++)
            Parser.ParseAddressAnswers(response, 0, length);

        var random = new Random(7);
        byte[] noise = new byte[256];
        for (int i = 0; i < 200; i++)
        {
            random.NextBytes(noise);
            noise[2] |= 0x80;      // set QR so the response check passes and the body is parsed
            noise[3] &= 0xF0;      // rcode = 0
            Parser.ParseAddressAnswers(noise, 0, noise.Length);
        }
    }

    [Fact]
    public void Compression_pointer_loop_is_survived()
    {
        // A name whose pointer points at itself: a malicious answer must not hang the parser.
        var message = new List<byte>();
        message.AddRange(Header(questionCount: 1, answerCount: 1));
        message.AddRange(EncodeName("example.com"));
        message.AddRange(BE16(TypeA));
        message.AddRange(BE16(1));
        int loopOffset = message.Count;
        message.AddRange(new byte[] { (byte)(0xC0 | (loopOffset >> 8)), (byte)loopOffset });

        Parser.ParseAddressAnswers(message.ToArray(), 0, message.Count);
    }

    // ---- wire-format helpers -----------------------------------------------------------------

    private const ushort TypeA = 1;
    private const ushort TypeCname = 5;
    private const ushort TypeAaaa = 28;

    private sealed record AnswerSpec(string Owner, ushort Type, byte[] RData, uint Ttl);

    private static AnswerSpec Answer(string owner, ushort type, IPAddress address, uint ttl)
        => new AnswerSpec(owner, type, address.GetAddressBytes(), ttl);

    private static AnswerSpec Cname(string owner, string target, uint ttl)
        => new AnswerSpec(owner, TypeCname, EncodeName(target).ToArray(), ttl);

    private static byte[] BuildResponse(string questionName, IReadOnlyList<AnswerSpec> answers, bool isResponse = true, int rcode = 0)
    {
        var message = new List<byte>();
        message.AddRange(Header(1, answers.Count, isResponse, rcode));
        message.AddRange(EncodeName(questionName));
        message.AddRange(BE16(TypeA));
        message.AddRange(BE16(1));

        foreach (AnswerSpec answer in answers)
        {
            message.AddRange(EncodeName(answer.Owner));
            message.AddRange(BE16(answer.Type));
            message.AddRange(BE16(1));
            message.AddRange(BE32(answer.Ttl));
            message.AddRange(BE16(answer.RData.Length));
            message.AddRange(answer.RData);
        }
        return message.ToArray();
    }

    private static IEnumerable<byte> Header(int questionCount, int answerCount, bool isResponse = true, int rcode = 0)
    {
        var header = new List<byte>();
        header.AddRange(BE16(0x1234));                                        // transaction id
        int flags = (isResponse ? 0x8000 : 0) | 0x0100 | (rcode & 0x0F);      // QR | RD | RCODE
        header.AddRange(BE16(flags));
        header.AddRange(BE16(questionCount));
        header.AddRange(BE16(answerCount));
        header.AddRange(BE16(0));                                             // authority count
        header.AddRange(BE16(0));                                             // additional count
        return header;
    }

    private static List<byte> EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (string label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return bytes;
    }

    private static byte[] BE16(int value) => new[] { (byte)(value >> 8), (byte)value };

    private static byte[] BE32(uint value)
        => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
}
