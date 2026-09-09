using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The Test button. It used to be a static method on the engine, so checking what it leaves behind
// meant starting a driver — which is how it came to leak a wireproxy subprocess per press without
// anything noticing.
public class OutboundTesterTests
{
    private static Outbound Socks5() => new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "socks5",
        Kind = OutboundKind.Socks5,
        Url = "socks5://127.0.0.1:1080",
    };

    private static (OutboundTester Tester, FakeOutboundSourceBuilder Builder) Build()
    {
        var builder = new FakeOutboundSourceBuilder(OutboundKind.Socks5);
        var factory = new OutboundSourceFactory(new[] { builder });
        return (new OutboundTester(factory, NullLoggerFactory.Instance), builder);
    }

    [Fact]
    public async Task AWayOutThatCannotConnect_IsReportedRatherThanThrown()
    {
        (OutboundTester tester, _) = Build();

        string? error = await tester.TestAsync(Socks5());

        Assert.NotNull(error);
        Assert.Contains("NotSupportedException", error);
    }

    [Fact]
    public async Task WhatTheTestBuilt_IsAlwaysPutDownAgain()
    {
        // This is the one that matters. A VPN test starts a wireproxy subprocess of its own, and
        // every press that failed to dispose it left another one holding a SOCKS port and a
        // WireGuard session for as long as the application ran.
        (OutboundTester tester, FakeOutboundSourceBuilder builder) = Build();

        await tester.TestAsync(Socks5());

        FakeBuild build = Assert.Single(builder.Builds);
        Assert.Equal(1, build.Source.Disposals);
    }

    [Fact]
    public async Task EachPress_BuildsItsOwnWayOutRatherThanBorrowingTheRunningOne()
    {
        // Built through the factory, which owns nothing, and not through the registry, which owns
        // the instance a VPN tunnel is running on. Two presses means two throwaways — and neither
        // of them is the one carrying traffic.
        (OutboundTester tester, FakeOutboundSourceBuilder builder) = Build();
        Outbound outbound = Socks5();

        await tester.TestAsync(outbound);
        await tester.TestAsync(outbound);

        Assert.Equal(2, builder.Builds.Count);
        Assert.All(builder.Builds, b => Assert.Equal(1, b.Source.Disposals));
    }

    [Fact]
    public async Task Block_SaysWhatItIsInsteadOfPretendingToDial()
    {
        (OutboundTester tester, FakeOutboundSourceBuilder builder) = Build();

        string? error = await tester.TestAsync(Outbound.CreateBlock());

        Assert.Equal("Block never connects anywhere.", error);
        Assert.Empty(builder.Builds);
    }
}
