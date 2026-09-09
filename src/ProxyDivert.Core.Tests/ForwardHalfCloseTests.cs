using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProxyDivert.Core.Outbounds.Extensions;
using TqkLibrary.Proxy.Interfaces;
using Xunit;

namespace ProxyDivert.Core.Tests;

/// <summary>
/// What the process is told when the far side of a tunnel stops speaking. The stream handed to the
/// transfer helper decorates the relay's socket, so the helper cannot find that socket to
/// half-close: the router has to say how. Without it a server that had finished its response left
/// the process waiting on one that was already over.
/// </summary>
public class ForwardHalfCloseTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task TheProcessIsHalfClosedWhenTheTunnelReachesEof()
    {
        var shutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Nothing to read and nothing coming: the tunnel side is finished the moment it is read.
        using var source = new StubConnectSource(Stream.Null);
        var client = new BlockingStream();

        Task forwarding = source.ForwardAsync(
            client, Guid.NewGuid(),
            shutdownClientSend: () => shutdown.TrySetResult(true));

        Assert.True(await shutdown.Task.WaitAsync(Patience));

        // Only the process's side is still open, so the transfer is still running — a half-close is
        // not a close, and a request already in flight must still be able to finish.
        Assert.False(forwarding.IsCompleted);
        client.Release();
        await forwarding.WaitAsync(Patience);
    }

    /// <summary>Reads block until released, the way a socket with a silent peer does.</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly SemaphoreSlim _released = new SemaphoreSlim(0, 1);

        public void Release() => _released.Release();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _released.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A tunnel that is already established and hands back the stream it was built with.</summary>
    private sealed class StubConnectSource : IConnectSource
    {
        private readonly Stream _stream;

        public StubConnectSource(Stream stream) => _stream = stream;

        public Task ConnectAsync(Uri address, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream> GetStreamAsync(CancellationToken cancellationToken = default) => Task.FromResult(_stream);
        public void Dispose() { }
    }
}
