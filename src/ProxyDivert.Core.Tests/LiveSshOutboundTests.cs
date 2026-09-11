using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Ssh;
using Renci.SshNet;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.Proxy.SshNet;
using TqkLibrary.Proxy.SshNet.Exceptions;
using Xunit;

namespace ProxyDivert.Core.Tests;

/// <summary>
/// A test that needs a real SSH server, and is skipped — reported as skipped, not passed — when none
/// is configured. Point it at one with:
/// <code>
/// PROXYDIVERT_SSH_TEST=ssh://user@127.0.0.1:22222   (the server; it must allow TCP forwarding)
/// PROXYDIVERT_SSH_TEST_KEY=C:\path\to\private_key   (optional when a password is given)
/// PROXYDIVERT_SSH_TEST_PASSWORD=...                  (the password, or the key's passphrase)
/// </code>
/// The destinations are echo listeners in this process on loopback, so the server has to be this
/// machine, or one that can reach back to it — a local sshd is the easy case.
/// </summary>
public sealed class LiveSshFactAttribute : FactAttribute
{
    public LiveSshFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LiveSshOutboundTests.ServerVariable)))
            Skip = $"Set {LiveSshOutboundTests.ServerVariable} to run against a real SSH server.";
    }
}

// The SSH outbound end to end: builder, known-hosts store, hardened SshNet source, a real session.
// Everything the offline tests cannot reach — the handshake, the host key check, direct-tcpip, the
// forwarder that only its own socket may use — is exercised here.
public sealed class LiveSshOutboundTests : IDisposable
{
    public const string ServerVariable = "PROXYDIVERT_SSH_TEST";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pd-live-ssh-" + Guid.NewGuid().ToString("N"));

    public LiveSshOutboundTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private string KnownHostsPath => Path.Combine(_folder, SshKnownHostsStore.FileName);

    private static Outbound Server() => new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "live ssh",
        Kind = OutboundKind.Ssh,
        Url = Environment.GetEnvironmentVariable(ServerVariable),
        PrivateKeyPath = Environment.GetEnvironmentVariable(ServerVariable + "_KEY"),
        Password = Environment.GetEnvironmentVariable(ServerVariable + "_PASSWORD"),
    };

    private IOutboundInstance Build(Outbound outbound)
        => new SshOutboundBuilder(new SshKnownHostsStore(KnownHostsPath))
            .Build(outbound, new OutboundBuildContext { Signature = OutboundSignature.Of(outbound) });

    [LiveSshFact]
    public async Task ASessionComesUpAndTheServerIsTrustedOnFirstUse()
    {
        await using IOutboundInstance instance = Build(Server());

        await instance.Tunnel!.StartAsync();

        Assert.True(instance.Tunnel.IsRunning);
        string recorded = Assert.Single(File.ReadAllLines(KnownHostsPath));
        Assert.Contains(" ssh-", recorded);
    }

    // The destination goes to the server as a NAME, and the server resolves it. The echo listens on
    // both loopbacks because which one "localhost" becomes is the server's choice — Windows' sshd
    // takes ::1 and does not fall back to 127.0.0.1.
    [LiveSshFact]
    public async Task ATunnelCarriesBytesToADestinationTheServerResolves()
    {
        using var echo = new EchoServer(IPAddress.Loopback, alsoIpv6Loopback: true);
        await using IOutboundInstance instance = Build(Server());

        string reply = await RoundTripAsync(instance.Source, new Uri($"tcp://localhost:{echo.Port}"), "hello over ssh");

        Assert.Equal("hello over ssh", reply);
    }

    // The router hands an IPv6 literal over as UriBuilder writes it — bracketed — and the tunnel
    // takes the brackets off before the server sees the name. Checked against Windows' sshd this
    // passes with or without that (its getaddrinfo accepts "[::1]"), so it pins that an IPv6
    // destination goes through end to end; the brackets matter to a server on glibc, which refuses
    // them as a name.
    [LiveSshFact]
    public async Task AnIpv6LiteralDestination_ReachesTheServerWithoutItsBrackets()
    {
        if (!Socket.OSSupportsIPv6) return;
        using var echo = new EchoServer(IPAddress.IPv6Loopback);
        await using IOutboundInstance instance = Build(Server());

        var target = new UriBuilder("tcp", IPAddress.IPv6Loopback.ToString(), echo.Port).Uri;
        Assert.StartsWith("[", target.Host);

        Assert.Equal("v6", await RoundTripAsync(instance.Source, target, "v6"));
    }

    // Many tunnels opening at once on one session. Two things used to break here: every tunnel adds
    // and removes a forwarded port on the same SshClient, whose list had no lock; and SSH.NET runs
    // each tunnel's blocking read loop on a thread-pool thread for the tunnel's whole life, so more
    // tunnels than the pool's minimum starved it — 40 at once timed out against a minimum of 32.
    // The minimum has to come back down once they are closed.
    [LiveSshFact]
    public async Task ManyTunnelsAtOnce_EachGetTheirOwnAnswer_AndGiveTheirThreadsBack()
    {
        ThreadPool.GetMinThreads(out int before, out _);
        using var echo = new EchoServer(IPAddress.Loopback);
        await using IOutboundInstance instance = Build(Server());
        await instance.Tunnel!.StartAsync();
        int count = before + 8;

        string[] replies = await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
            RoundTripAsync(instance.Source, new Uri($"tcp://127.0.0.1:{echo.Port}"), "tunnel " + i)));

        for (int i = 0; i < replies.Length; i++) Assert.Equal("tunnel " + i, replies[i]);
        ThreadPool.GetMinThreads(out int after, out _);
        Assert.Equal(before, after);
    }

    // The forwarder is a listening loopback port for as long as the tunnel lives. Another process
    // finding it must be turned away before a channel is opened — and the tunnel must not notice.
    [LiveSshFact]
    public async Task SomebodyElseConnectingToTheForwarder_IsTurnedAway()
    {
        using var echo = new EchoServer(IPAddress.Loopback);
        await using IOutboundInstance instance = Build(Server());

        using IConnectSource tunnel = await instance.Source.GetConnectSourceAsync(Guid.NewGuid());
        await tunnel.ConnectAsync(new Uri($"tcp://127.0.0.1:{echo.Port}"));
        Stream stream = await tunnel.GetStreamAsync();

        var port = (ForwardedPortLocal)typeof(SshNetConnectSource)
            .GetField("_port", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tunnel)!;
        using (var intruder = new TcpClient())
        {
            await intruder.ConnectAsync(IPAddress.Loopback, (int)port.BoundPort);
            NetworkStream sneaky = intruder.GetStream();
            await sneaky.WriteAsync(Encoding.ASCII.GetBytes("let me in\n"));
            sneaky.ReadTimeout = 5000;
            // Closed by the forwarder: nothing comes back, least of all an echo from the server side.
            Assert.Equal(0, await ReadSomeAsync(sneaky));
        }

        Assert.Equal("still mine", await EchoAsync(stream, "still mine"));
    }

    // The point of the store: a server that shows a different key is refused, with the reason and
    // the file, not with SSH.NET's bare "key exchange negotiation failed".
    [LiveSshFact]
    public async Task AServerShowingADifferentKey_IsRefusedWithTheReason()
    {
        Outbound outbound = Server();
        OutboundAddressHost(outbound, out string host, out int port);
        // A made-up key recorded for the very server under test.
        File.WriteAllText(KnownHostsPath,
            $"{SshKnownHostsStore.HostEntry(host, port)} ssh-ed25519 {Convert.ToBase64String(new byte[51])}{Environment.NewLine}");
        await using IOutboundInstance instance = Build(outbound);

        var error = await Assert.ThrowsAsync<SshNetHostKeyRejectedException>(() => instance.Tunnel!.StartAsync());

        Assert.Contains(KnownHostsPath, error.Message);
        Assert.False(instance.Tunnel!.IsRunning);
    }

    private static void OutboundAddressHost(Outbound outbound, out string host, out int port)
    {
        host = outbound.Address!.Host!;
        port = outbound.Address.Port;
    }

    private static async Task<string> RoundTripAsync(IProxySource source, Uri target, string text)
    {
        using IConnectSource tunnel = await source.GetConnectSourceAsync(Guid.NewGuid());
        await tunnel.ConnectAsync(target);
        return await EchoAsync(await tunnel.GetStreamAsync(), text);
    }

    private static async Task<string> EchoAsync(Stream stream, string text)
    {
        byte[] line = Encoding.UTF8.GetBytes(text + "\n");
        await stream.WriteAsync(line);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new MemoryStream();
        byte[] buffer = new byte[256];
        while (!received.ToArray().Contains((byte)'\n'))
        {
            int read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            received.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(received.ToArray()).TrimEnd('\n');
    }

    private static async Task<int> ReadSomeAsync(Stream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return await stream.ReadAsync(new byte[64], cts.Token); }
        catch (IOException) { return 0; }
    }

    // Echoes whatever each connection sends, one connection per task, until disposed. Optionally on
    // [::1] as well, on the same port number.
    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpListener? _v6Listener;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        public EchoServer(IPAddress address, bool alsoIpv6Loopback = false)
        {
            _listener = new TcpListener(address, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(_listener);

            if (alsoIpv6Loopback && Socket.OSSupportsIPv6)
            {
                _v6Listener = new TcpListener(IPAddress.IPv6Loopback, Port);
                _v6Listener.Start();
                _ = AcceptLoopAsync(_v6Listener);
            }
        }

        public int Port { get; }

        private async Task AcceptLoopAsync(TcpListener listener)
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try { await client.GetStream().CopyToAsync(client.GetStream(), _stop.Token); } catch { }
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _v6Listener?.Stop();
        }
    }
}
