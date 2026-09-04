using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.WinDivert.Inspection;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Regression tests for the peek path.
//
// The bug these exist for: PeekAsync used to read until the buffer was FULL. A client sends its
// first flight (~500 bytes for a TLS ClientHello) and then waits for the server, so asking for
// 2 KB blocked until the peek timed out — the routing layer never saw the SNI and every TLS
// connection fell back to IP-only routing. It looked like "SNI parsing does not work" while the
// parser was fine and the name was sitting unread in the buffer.
public class PeekableStreamTests
{
    [Fact]
    public async Task Peek_returns_what_arrived_without_waiting_for_a_full_buffer()
    {
        byte[] payload = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");
        using var inner = new OneShotStream(payload);
        var stream = new PeekableStream(inner);

        var watch = Stopwatch.StartNew();
        int available = await stream.PeekAsync(2048, CancellationToken.None);
        watch.Stop();

        Assert.Equal(payload.Length, available);
        // The stream deliberately never sends more; anything close to the 5s guard means the peek
        // was waiting for a full buffer again.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"peek took {watch.Elapsed}");
    }

    [Fact]
    public async Task Peeked_bytes_are_still_delivered_to_the_reader()
    {
        byte[] payload = Encoding.ASCII.GetBytes("first flight");
        using var inner = new OneShotStream(payload);
        var stream = new PeekableStream(inner);

        await stream.PeekAsync(2048, CancellationToken.None);

        byte[] buffer = new byte[payload.Length];
        int read = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);

        Assert.Equal(payload.Length, read);
        Assert.Equal("first flight", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task The_host_header_is_readable_from_a_single_flight()
    {
        byte[] payload = Encoding.ASCII.GetBytes("GET /index.html HTTP/1.1\r\nHost: www.example.com:8080\r\n\r\n");
        using var inner = new OneShotStream(payload);
        var stream = new PeekableStream(inner);

        int available = await stream.PeekAsync(1024, CancellationToken.None);

        Assert.True(new HttpHostParser().TryReadHostName(stream.PeekBuffer, available, out string host));
        Assert.Equal("www.example.com", host);
    }

    [Fact]
    public async Task Peeking_twice_extends_the_window_instead_of_restarting_it()
    {
        using var inner = new TwoShotStream("AAAA"u8.ToArray(), "BBBB"u8.ToArray());
        var stream = new PeekableStream(inner);

        int first = await stream.PeekAsync(64, CancellationToken.None);
        int second = await stream.PeekAsync(64, CancellationToken.None);

        Assert.Equal(4, first);
        Assert.Equal(8, second);
        Assert.Equal("AAAABBBB", Encoding.ASCII.GetString(stream.PeekBuffer, 0, second));
    }

    // Delivers one chunk, then behaves like a peer that is waiting for a reply: the read never
    // completes on its own.
    private sealed class OneShotStream : Stream
    {
        private readonly byte[] _payload;
        private bool _sent;

        public OneShotStream(byte[] payload) => _payload = payload;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!_sent)
            {
                _sent = true;
                int n = Math.Min(count, _payload.Length);
                Buffer.BlockCopy(_payload, 0, buffer, offset, n);
                return n;
            }
            // A 5 second guard rather than an infinite wait, so a regression fails the test
            // instead of hanging the whole run.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TwoShotStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _index;

        public TwoShotStream(params byte[][] chunks) => _chunks = chunks;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_index >= _chunks.Length) return Task.FromResult(0);
            byte[] chunk = _chunks[_index++];
            int n = Math.Min(count, chunk.Length);
            Buffer.BlockCopy(chunk, 0, buffer, offset, n);
            return Task.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
