using System;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.WinDivert.SecureDns;
using TqkLibrary.WinDivert.SecureDns.Interfaces;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// Hands every lookup to a resolver and reports whether it got an answer to the outbound's
/// <see cref="DohHealth"/>. A lookup the caller cancelled, or that ended after this resolver was
/// disposed, says nothing about the endpoint and is not reported. Owns the inner resolver:
/// disposing this disposes it.
/// </summary>
internal sealed class MonitoredDnsResolver : IDnsResolver
{
    private readonly IDnsResolver _inner;
    private readonly DohHealth _health;
    private volatile bool _disposed;

    public MonitoredDnsResolver(IDnsResolver inner, DohHealth health)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _health = health ?? throw new ArgumentNullException(nameof(health));
    }

    public Uri Endpoint => _inner.Endpoint;

    public async Task<byte[]?> ResolveAsync(byte[] dnsWireQuery, CancellationToken ct)
    {
        byte[]? answer;
        try
        {
            answer = await _inner.ResolveAsync(dnsWireQuery, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_disposed && !ct.IsCancellationRequested)
                _health.OnFailure(ex.GetType().Name + ": " + ex.Message);
            throw;
        }

        if (ct.IsCancellationRequested || _disposed) return answer;
        if (answer is { Length: > 0 }) _health.OnSuccess();
        else _health.OnFailure((_inner as DohResolver)?.LastFailureReason ?? "no answer");
        return answer;
    }

    public void Dispose()
    {
        _disposed = true;
        _inner.Dispose();
    }
}
