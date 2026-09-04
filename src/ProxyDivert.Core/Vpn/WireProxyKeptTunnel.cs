using System;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.Proxy.Vpn.WireProxyCli;

namespace ProxyDivert.Core.Vpn;

// The wireproxy tunnel seen as something the engine can hold open.
//
// WireGuardProxySource lives in TqkLibrary.Proxy and knows nothing about ProxyDivert, so the shape
// the supervisor wants is put on it from this side rather than by changing the library. The wrapper
// holds no state of its own, which is what lets the supervisor make one whenever it needs it: every
// event subscription lives and dies inside a single WaitUntilDownAsync call.
internal sealed class WireProxyKeptTunnel : IKeptTunnel
{
    // The exit event is the fast path; this is the backstop for a process that stops being ours
    // without the event arriving.
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(15);

    private readonly WireGuardProxySource _source;

    public WireProxyKeptTunnel(WireGuardProxySource source)
        => _source = source ?? throw new ArgumentNullException(nameof(source));

    public bool IsRunning => _source.IsRunning;

    public string Endpoint => _source.Socks5Endpoint?.ToString() ?? "(not listening)";

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _source.StartAsync(cancellationToken);

    public async Task<string> WaitUntilDownAsync(CancellationToken cancellationToken = default)
    {
        var down = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? sender, WireProxyExitedEventArgs e) => down.TrySetResult(Describe(e));

        _source.Exited += OnExited;
        try
        {
            // Subscribing cannot catch an exit that already happened, so check once before waiting.
            if (!_source.IsRunning) return "the wireproxy process is gone";

            while (!cancellationToken.IsCancellationRequested)
            {
                Task delay = Task.Delay(HealthPollInterval, cancellationToken);
                Task first = await Task.WhenAny(down.Task, delay).ConfigureAwait(false);

                if (ReferenceEquals(first, down.Task)) return await down.Task.ConfigureAwait(false);
                if (delay.IsCanceled) break;
                if (!_source.IsRunning) return "the wireproxy process is gone";
            }
            return "cancelled";
        }
        finally
        {
            _source.Exited -= OnExited;
        }
    }

    private static string Describe(WireProxyExitedEventArgs e)
    {
        string message = $"wireproxy exited with code {e.ExitCode}";
        // stderr can be a whole startup transcript; the last line is the one that says what broke.
        string[] lines = e.StandardError.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? message : $"{message}: {lines[lines.Length - 1].Trim()}";
    }
}
