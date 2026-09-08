using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;

namespace ProxyDivert.Core.Tests;

// A way out that builds instantly and connects to nothing.
//
// This is the seam the per-kind builders bought. Everything below the registry used to be a real
// SOCKS source or a real wireproxy subprocess, so the supervisor could only ever be tested on its
// failure path — a .conf that is not there. Now the kind under test is whatever this says it is.
internal sealed class FakeOutboundSourceBuilder : IOutboundSourceBuilder
{
    private readonly Func<Outbound, FakeProxySource> _source;
    private readonly ConcurrentQueue<FakeBuild> _builds = new ConcurrentQueue<FakeBuild>();

    /// <summary>A kind that holds nothing open, the way a SOCKS or HTTP way out does.</summary>
    public FakeOutboundSourceBuilder(OutboundKind kind)
        : this(kind, _ => new FakeProxySource(), buildsManagedSource: false)
    {
    }

    /// <summary>
    /// A kind that keeps something up between requests. Handing over a managed source is what makes
    /// this builder answer <see cref="BuildsManagedSource"/> with true — the two cannot disagree,
    /// which is the mistake a supervisor filtering on its own rule used to be able to make.
    /// </summary>
    public FakeOutboundSourceBuilder(OutboundKind kind, Func<Outbound, FakeManagedProxySource> tunnel)
        : this(kind, tunnel, buildsManagedSource: true)
    {
    }

    private FakeOutboundSourceBuilder(
        OutboundKind kind, Func<Outbound, FakeProxySource> source, bool buildsManagedSource)
    {
        Kind = kind;
        _source = source;
        BuildsManagedSource = buildsManagedSource;
    }

    public OutboundKind Kind { get; }

    public bool BuildsManagedSource { get; }

    /// <summary>Every build this has been asked for, oldest first.</summary>
    public IReadOnlyList<FakeBuild> Builds => _builds.ToArray();

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
    {
        FakeProxySource source = _source(outbound);
        _builds.Enqueue(new FakeBuild(outbound.Id, context.Signature, context.WireProxyPath, source));
        return new OutboundInstance(outbound.Id, context.Signature, source);
    }
}

internal sealed record FakeBuild(Guid OutboundId, string Signature, string? WireProxyPath, FakeProxySource Source);

internal class FakeProxySource : IProxySource
{
    private int _disposals;

    /// <summary>How many times this instance has been released. More than one is a double free.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    public bool IsSupportUdp => false;

    public bool IsSupportIpv6 { get; set; }

    public bool IsSupportBind => false;

    public Task<IConnectSource> GetConnectSourceAsync(Guid tunnelId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Nothing in these tests opens a tunnel.");

    public Task<IBindSource> GetBindSourceAsync(Guid tunnelId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Nothing in these tests opens a tunnel.");

    public Task<IUdpAssociateSource> GetUdpAssociateSourceAsync(Guid tunnelId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Nothing in these tests opens a tunnel.");

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposals);
        return default;
    }
}

// The same way out, but one that holds something open: it comes up at once and then stays up until
// the test says otherwise. Being a source rather than a thing wrapped around one is the point — that
// is how the supervisor finds it, and how it finds a real wireproxy or VPN driver too.
internal sealed class FakeManagedProxySource : FakeProxySource, IManagedProxySource
{
    private readonly TaskCompletionSource<string> _down =
        new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _starts;

    /// <summary>How many times the supervisor has brought this up.</summary>
    public int Starts => Volatile.Read(ref _starts);

    public bool IsRunning { get; private set; }

    public string Endpoint => "127.0.0.1:9";

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _starts);
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task<string> WaitUntilDownAsync(CancellationToken cancellationToken = default) => _down.Task;

    /// <summary>Makes the way out finish for good, the way a dead subprocess would.</summary>
    public void GoDown(string reason)
    {
        IsRunning = false;
        _down.TrySetResult(reason);
    }
}
