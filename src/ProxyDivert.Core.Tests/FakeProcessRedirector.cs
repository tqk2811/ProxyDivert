using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using TqkLibrary.WinDivert.Flow.Interfaces;
using TqkLibrary.WinDivert.Flow.Models;
using TqkLibrary.WinDivert.Redirect.Interfaces;
using TqkLibrary.WinDivert.Redirect.Models;
using TqkLibrary.WinDivert.SecureDns;
using TqkLibrary.WinDivert.SecureDns.Interfaces;

namespace ProxyDivert.Core.Tests;

/// <summary>
/// A redirect session with no driver behind it: the reverse-DNS table is real, everything a test
/// asks of it is recorded, and nothing opens a handle.
/// </summary>
/// <remarks>
/// It exists because the routing path takes an <see cref="IProcessRedirector"/> for two small
/// things — the names DNS taught it, and the socket UDP replies are injected through — and neither
/// needs WinDivert to be loaded, let alone the test to run elevated.
/// </remarks>
internal sealed class FakeProcessRedirector : IProcessRedirector
{
    private readonly HashSet<uint> _tracked = new HashSet<uint>();

    public INatTable Nat => throw new NotSupportedException("no NAT table in the fake redirector");

    public int TcpRelayPort => 51000;
    public int UdpRelayPort => 51001;
    public int TcpRelayPortV6 => 51002;
    public int UdpRelayPortV6 => 51003;

    public IReverseDnsTable ReverseDns { get; } = new ReverseDnsTable();

    public IDnsCacheLookup? DnsLookup => null;

    public IReadOnlyCollection<uint> TrackedProcessIds => _tracked;

    /// <summary>The UDP replies a test's routing pushed back at the process.</summary>
    public List<(ushort Port, byte[] Payload, bool IsIpv6)> Injected { get; } = new List<(ushort, byte[], bool)>();

    public int ResetEscapedFlowsCalls { get; private set; }

    public event Action<FlowKey>? TcpConnectEstablished;
    public event Action<FlowKey>? TcpConnectClosed;
    public event Action<RedirectedTcpConnection>? TcpConnectionOpened;
    public event Action<RedirectedTcpConnection>? TcpConnectionClosed;

    public void Start() { }

    public void AddTrackedProcessId(uint pid) => _tracked.Add(pid);

    public bool RemoveTrackedProcessId(uint pid) => _tracked.Remove(pid);

    public bool IsTrackedProcessId(uint pid) => _tracked.Contains(pid);

    public int ResetEscapedFlows()
    {
        ResetEscapedFlowsCalls++;
        return 0;
    }

    public Task InjectUdpReplyToProcessAsync(ushort processClientPort, byte[] payload, bool isIpv6 = false)
    {
        Injected.Add((processClientPort, payload, isIpv6));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // The events are declared by the interface and nothing here raises them; naming them keeps
        // the compiler from warning that they are never used.
        TcpConnectEstablished = null;
        TcpConnectClosed = null;
        TcpConnectionOpened = null;
        TcpConnectionClosed = null;
    }
}
