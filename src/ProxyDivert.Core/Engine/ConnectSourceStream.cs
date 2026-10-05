using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TqkLibrary.Proxy.Interfaces;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// The stream of a tunnel opened through an outbound, handed to someone who only knows how to close
/// a stream — an <c>HttpClient</c> pooling connections, for one.
/// </summary>
/// <remarks>
/// Owns the connect source: disposing the stream disposes the source with it, which is what closes
/// the tunnel. Without this the pool would close the stream and leave the tunnel behind it open.
/// </remarks>
internal sealed class ConnectSourceStream : Stream
{
    private readonly Stream _inner;
    private readonly IConnectSource _owner;
    private int _disposed;

    public ConnectSourceStream(Stream inner, IConnectSource owner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>
    /// Opens a tunnel through <paramref name="source"/> to <paramref name="host"/>:<paramref name="port"/>
    /// and returns its stream. On failure the half-opened tunnel is closed before the exception
    /// leaves.
    /// </summary>
    public static async Task<Stream> OpenAsync(IProxySource source, string host, int port, CancellationToken ct, ILogger? logger = null)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        long startedAt = Stopwatch.GetTimestamp();
        IConnectSource connectSource = await source.GetConnectSourceAsync(Guid.NewGuid(), ct).ConfigureAwait(false);
        try
        {
            // UriBuilder brackets an IPv6 literal, which is what the proxy address parsers expect.
            await connectSource.ConnectAsync(new UriBuilder("tcp", host, port).Uri, ct).ConfigureAwait(false);
            Stream stream = await connectSource.GetStreamAsync(ct).ConfigureAwait(false);
            if (logger is not null && logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("connected to {Host}:{Port} through the outbound in {Ms} ms",
                    host, port, (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency);
            return new ConnectSourceStream(stream, connectSource);
        }
        catch (Exception ex)
        {
            if (logger is not null && logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("connecting to {Host}:{Port} through the outbound failed after {Ms} ms: {Error}",
                    host, port, (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency, ex.Message);
            try { connectSource.Dispose(); } catch { }
            throw;
        }
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanTimeout => _inner.CanTimeout;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
    public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _inner.Dispose(); } catch { }
            try { _owner.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { await _inner.DisposeAsync().ConfigureAwait(false); } catch { }
            try { _owner.Dispose(); } catch { }
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
