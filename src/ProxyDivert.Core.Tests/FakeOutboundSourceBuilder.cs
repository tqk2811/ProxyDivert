using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn;
using TqkLibrary.Proxy.Interfaces;

namespace ProxyDivert.Core.Tests;

// A way out that builds instantly and connects to nothing.
//
// This is the seam the per-kind builders bought. Everything below the registry used to be a real
// SOCKS source or a real wireproxy subprocess, so the supervisor could only ever be tested on its
// failure path — a .conf that is not there. Now the kind under test is whatever this says it is.
internal sealed class FakeOutboundSourceBuilder : IOutboundSourceBuilder
{
    private readonly Func<Outbound, IKeptTunnel?>? _tunnel;
    private readonly ConcurrentQueue<FakeBuild> _builds = new ConcurrentQueue<FakeBuild>();

    public FakeOutboundSourceBuilder(OutboundKind kind, Func<Outbound, IKeptTunnel?>? tunnel = null)
    {
        Kind = kind;
        _tunnel = tunnel;
    }

    public OutboundKind Kind { get; }

    /// <summary>Every build this has been asked for, oldest first.</summary>
    public IReadOnlyList<FakeBuild> Builds => _builds.ToArray();

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
    {
        var source = new FakeProxySource();
        _builds.Enqueue(new FakeBuild(outbound.Id, context.Signature, context.WireProxyPath, source));
        return new OutboundInstance(outbound.Id, context.Signature, source, _tunnel?.Invoke(outbound));
    }
}

internal sealed record FakeBuild(Guid OutboundId, string Signature, string? WireProxyPath, FakeProxySource Source);

internal sealed class FakeProxySource : IProxySource
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

// A tunnel that comes up at once and then stays up until the test says otherwise.
internal sealed class FakeKeptTunnel : IKeptTunnel
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

    /// <summary>Makes the tunnel finish for good, the way a dead subprocess would.</summary>
    public void GoDown(string reason)
    {
        IsRunning = false;
        _down.TrySetResult(reason);
    }
}
