using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Engine;
using TqkLibrary.WinDivert.SecureDns.Interfaces;
using Xunit;

namespace ProxyDivert.Core.Tests;

// What a lookup tells the outbound's DoH health, and what it must not.
public class MonitoredDnsResolverTests
{
    private static readonly byte[] Query = { 1, 2, 3 };

    private sealed class StubResolver : IDnsResolver
    {
        public Func<CancellationToken, Task<byte[]?>> OnResolve { get; set; } = _ => Task.FromResult<byte[]?>(new byte[] { 9 });

        public bool Disposed { get; private set; }

        public Uri Endpoint { get; } = new Uri("https://dns.example/dns-query");

        public Task<byte[]?> ResolveAsync(byte[] dnsWireQuery, CancellationToken ct) => OnResolve(ct);

        public void Dispose() => Disposed = true;
    }

    private sealed class LineLogger : ILogger
    {
        private readonly List<string> _lines;

        public LineLogger(List<string> lines) => _lines = lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Add(logLevel + ": " + formatter(state, exception));
    }

    // Threshold 1: every reported failure shows up as a Warning, every success after one as Information.
    private static (MonitoredDnsResolver Resolver, StubResolver Inner, List<string> Lines) Create()
    {
        var inner = new StubResolver();
        var lines = new List<string>();
        var health = new DohHealth("out", new LineLogger(lines), failureThreshold: 1);
        return (new MonitoredDnsResolver(inner, health), inner, lines);
    }

    [Fact]
    public async Task Null_answer_is_a_failure()
    {
        var (resolver, inner, lines) = Create();
        inner.OnResolve = _ => Task.FromResult<byte[]?>(null);

        Assert.Null(await resolver.ResolveAsync(Query, CancellationToken.None));

        Assert.Single(lines);
        Assert.StartsWith("Warning", lines[0]);
    }

    [Fact]
    public async Task Answer_after_a_failure_is_a_recovery()
    {
        var (resolver, inner, lines) = Create();
        inner.OnResolve = _ => Task.FromResult<byte[]?>(null);
        await resolver.ResolveAsync(Query, CancellationToken.None);
        inner.OnResolve = _ => Task.FromResult<byte[]?>(new byte[] { 9 });

        Assert.NotNull(await resolver.ResolveAsync(Query, CancellationToken.None));

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("Information", lines[1]);
    }

    [Fact]
    public async Task Exception_is_rethrown_and_recorded_as_a_failure()
    {
        var (resolver, inner, lines) = Create();
        inner.OnResolve = _ => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Query, CancellationToken.None));

        Assert.Single(lines);
        Assert.Contains("boom", lines[0]);
    }

    [Fact]
    public async Task Cancellation_is_rethrown_and_not_recorded()
    {
        var (resolver, inner, lines) = Create();
        using var cts = new CancellationTokenSource();
        inner.OnResolve = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

        Task<byte[]?> pending = resolver.ResolveAsync(Query, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task A_null_answer_to_a_cancelled_caller_is_not_recorded()
    {
        var (resolver, inner, lines) = Create();
        using var cts = new CancellationTokenSource();
        inner.OnResolve = _ =>
        {
            cts.Cancel();
            return Task.FromResult<byte[]?>(null);
        };

        Assert.Null(await resolver.ResolveAsync(Query, cts.Token));

        Assert.Empty(lines);
    }

    [Fact]
    public async Task Nothing_is_reported_after_dispose()
    {
        var (resolver, inner, lines) = Create();
        inner.OnResolve = async _ =>
        {
            await Task.Yield();
            resolver.Dispose();
            return null;
        };

        Assert.Null(await resolver.ResolveAsync(Query, CancellationToken.None));

        Assert.Empty(lines);
    }

    [Fact]
    public void Endpoint_and_dispose_go_to_the_inner_resolver()
    {
        var (resolver, inner, _) = Create();

        Assert.Same(inner.Endpoint, resolver.Endpoint);
        resolver.Dispose();
        Assert.True(inner.Disposed);
    }
}
