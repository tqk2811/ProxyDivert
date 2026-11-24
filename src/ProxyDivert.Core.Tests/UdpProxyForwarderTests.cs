using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Engine;
using TqkLibrary.Proxy.Interfaces;
using Xunit;

namespace ProxyDivert.Core.Tests;

// A22: a UDP ASSOCIATE that does not end up in use must still be closed. The control connection to
// the proxy and the UDP socket are open from the moment the source hands the tunnel over, so a
// refusal, a cancellation, or an answer arriving after the forwarder gave up waiting each used to
// leave both open with nobody holding them.
public class UdpProxyForwarderTests
{
    private static readonly IPEndPoint Destination = new IPEndPoint(IPAddress.Loopback, 53);

    [Fact]
    public async Task AssociateRefused_ClosesTheTunnel()
    {
        var tunnel = new FakeUdpTunnel(_ => throw new IOException("the proxy refused UDP"));
        await using var forwarder = Forwarder();

        forwarder.Send(Guid.NewGuid(), new FakeUdpProxySource(tunnel), 5000, Destination, new byte[1], false);

        await tunnel.WaitDisposedAsync();
        Assert.Equal(1, tunnel.Disposals);
    }

    [Fact]
    public async Task AssociateCancelledByDispose_ClosesTheTunnel()
    {
        var tunnel = new FakeUdpTunnel(ct => Task.Delay(Timeout.Infinite, ct));
        var forwarder = Forwarder();

        forwarder.Send(Guid.NewGuid(), new FakeUdpProxySource(tunnel), 5000, Destination, new byte[1], false);
        await tunnel.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await forwarder.DisposeAsync();

        await tunnel.WaitDisposedAsync();
        Assert.Equal(1, tunnel.Disposals);
    }

    [Fact]
    public async Task AssociateFinishingAfterDispose_ClosesTheTunnel()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ignores the token, the way a proxy that answers late does.
        var tunnel = new FakeUdpTunnel(_ => release.Task);
        var forwarder = Forwarder();

        forwarder.Send(Guid.NewGuid(), new FakeUdpProxySource(tunnel), 5000, Destination, new byte[1], false);
        await tunnel.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await forwarder.DisposeAsync();
        release.SetResult();

        await tunnel.WaitDisposedAsync();
        await Task.Delay(100);
        Assert.Equal(1, tunnel.Disposals);
    }

    private static UdpProxyForwarder Forwarder()
        => new UdpProxyForwarder(new FakeProcessRedirector(), NullLogger<UdpProxyForwarder>.Instance, CancellationToken.None);

    private sealed class FakeUdpProxySource : IProxySource, IUdpCapable
    {
        private readonly FakeUdpTunnel _tunnel;

        public FakeUdpProxySource(FakeUdpTunnel tunnel) => _tunnel = tunnel;

        public bool IsSupportUdp => true;

        public Task<IUdpAssociateSource> GetUdpAssociateSourceAsync(Guid tunnelId, CancellationToken cancellationToken = default)
            => Task.FromResult<IUdpAssociateSource>(_tunnel);

        public Task<IConnectSource> GetConnectSourceAsync(Guid tunnelId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => default;
    }

    private sealed class FakeUdpTunnel : IUdpAssociateSource
    {
        private readonly Func<CancellationToken, Task> _associate;
        private readonly TaskCompletionSource _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposals;

        public FakeUdpTunnel(Func<CancellationToken, Task> associate) => _associate = associate;

        public Task Started => _started.Task;

        public int Disposals => Volatile.Read(ref _disposals);

        public IPEndPoint? RelayEndPoint => null;

        public IPEndPoint? LocalEndPoint => null;

        public async Task<IPEndPoint> AssociateAsync(CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _associate(cancellationToken).ConfigureAwait(false);
            return new IPEndPoint(IPAddress.Loopback, 1080);
        }

        public Task SendAsync(IPEndPoint destination, byte[] payload, int offset, int count, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task<UdpAssociateDatagram> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException();
        }

        public Task<Stream> GetStreamAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WaitDisposedAsync() => _disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            _disposed.TrySetResult();
        }
    }
}
