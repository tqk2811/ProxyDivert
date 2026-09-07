using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TqkLibrary.WinDivert.Flow.Interfaces;
using TqkLibrary.WinDivert.Flow.Models;
using TqkLibrary.WinDivert.Native.Enums;
using TqkLibrary.WinDivert.Native.Models;
using TqkLibrary.WinDivert.Packet;
using TqkLibrary.WinDivert.Packet.Models;
using TqkLibrary.WinDivert.Pipeline.Enums;
using TqkLibrary.WinDivert.Pipeline.Interfaces;
using TqkLibrary.WinDivert.Pipeline.Models;
using TqkLibrary.WinDivert.Redirect;
using TqkLibrary.WinDivert.Redirect.Enums;
using TqkLibrary.WinDivert.Redirect.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The NAT stage meeting a packet of a flow that was open before capture. Passed through by default,
// and answered with a reset once the host has asked for that flow — what a save does to the
// connections a browser already had when it was attached.
public class NatRedirectMiddlewareEscapedFlowTests
{
    private static readonly IPAddress Client = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Server = IPAddress.Parse("93.184.216.34");
    private const ushort ClientPort = 51000;
    private const int RelayPort = 8037;
    private const uint Pid = 4242;

    private static FlowKey TheFlow => new FlowKey(6, Client, ClientPort, Server, 443);

    private static byte[] DataPacket()
        => RawTcpPackets.Build(Client, ClientPort, Server, 443,
            sequence: 1000, acknowledgment: 5000, flags: RawTcpPackets.Ack | RawTcpPackets.Psh, payloadLength: 10);

    private static byte[] SynPacket()
        => RawTcpPackets.Build(Client, ClientPort, Server, 443,
            sequence: 999, acknowledgment: 0, flags: RawTcpPackets.Syn, payloadLength: 0);

    private static PacketContext Context(byte[] packet, IPacketInjector injector)
    {
        var ctx = new PacketContext(packet, injector, CancellationToken.None)
        {
            Length = packet.Length,
            Packet = PacketParser.Default.TryParse(packet, packet.Length),
        };
        WinDivertAddress addr = default;
        addr.Layer = WinDivertLayer.Network;
        addr.Outbound = true;
        addr.Loopback = false;
        addr.Network.IfIdx = 9;
        addr.Network.SubIfIdx = 0;
        ctx.Address = addr;
        return ctx;
    }

    private static NatRedirectMiddleware Middleware(NatTable nat, FakeTracker tracker, EscapedFlowBlocklist? flowsToReset)
        => new NatRedirectMiddleware(
            nat, tracker, RelayPorts.Ipv4Only(RelayPort, 0), RedirectProtocol.Tcp, rootProcessId: 0,
            NullLogger<NatRedirectMiddleware>.Instance, flowsToReset: flowsToReset);

    [Fact]
    public async Task A_flow_the_host_asked_to_reset_is_answered_with_an_rst_and_its_packet_dropped()
    {
        var tracker = new FakeTracker(TheFlow);
        var injector = new RecordingInjector();
        var flowsToReset = new EscapedFlowBlocklist();
        flowsToReset.Add(TheFlow);
        NatRedirectMiddleware middleware = Middleware(new NatTable(), tracker, flowsToReset);
        PacketContext ctx = Context(DataPacket(), injector);
        bool nextCalled = false;

        await middleware.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.Equal(PacketDisposition.Drop, ctx.Disposition);
        Assert.False(nextCalled);

        (byte[] packet, WinDivertAddress addr) = Assert.Single(injector.Packets);
        ParsedPacket reset = PacketParser.Default.TryParse(packet, packet.Length)!;
        Assert.True(reset.Tcp.Rst);
        Assert.Equal(Server, reset.Source);
        Assert.Equal(Client, reset.Destination);
        Assert.Equal(ClientPort, reset.DestinationPort);
        Assert.Equal(5000u, reset.Tcp.SequenceNumber);
        Assert.False(addr.Outbound);
        Assert.False(addr.Loopback);
        Assert.False(addr.IPv6);
        Assert.Equal(9u, addr.Network.IfIdx);
    }

    // The default for a pre-existing flow is unchanged: it passes, and the host is warned once.
    [Fact]
    public async Task A_flow_nobody_asked_about_still_passes_through()
    {
        var tracker = new FakeTracker(TheFlow);
        var injector = new RecordingInjector();
        NatRedirectMiddleware middleware = Middleware(new NatTable(), tracker, new EscapedFlowBlocklist());
        PacketContext ctx = Context(DataPacket(), injector);
        bool nextCalled = false;

        await middleware.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.Equal(PacketDisposition.Pass, ctx.Disposition);
        Assert.True(nextCalled);
        Assert.Empty(injector.Packets);
    }

    // A SYN is never an escaped flow, listed or not: it is captured and redirected like any new
    // connection. This is what keeps a flow whose SYN was still in flight when the list was built
    // from being reset by mistake.
    [Fact]
    public async Task A_syn_is_redirected_even_when_the_flow_is_listed()
    {
        var tracker = new FakeTracker(TheFlow);
        var injector = new RecordingInjector();
        var flowsToReset = new EscapedFlowBlocklist();
        flowsToReset.Add(TheFlow);
        var nat = new NatTable();
        NatRedirectMiddleware middleware = Middleware(nat, tracker, flowsToReset);
        PacketContext ctx = Context(SynPacket(), injector);

        await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);

        Assert.Equal(PacketDisposition.Modified, ctx.Disposition);
        Assert.Empty(injector.Packets);
        Assert.NotNull(nat.Find(6, ClientPort, isIpv6: false));
        Assert.Equal(RelayPort, ctx.Packet!.DestinationPort);
    }

    private sealed class RecordingInjector : IPacketInjector
    {
        public List<(byte[] Packet, WinDivertAddress Address)> Packets { get; } = new();

        public bool Inject(byte[] buffer, int length, in WinDivertAddress addr)
        {
            byte[] copy = new byte[length];
            Buffer.BlockCopy(buffer, 0, copy, 0, length);
            Packets.Add((copy, addr));
            return true;
        }
    }

    // Knows exactly one TCP flow, owned by one pid, and never learns another.
    private sealed class FakeTracker : ISocketTracker
    {
        private readonly FlowKey _flow;

        public FakeTracker(FlowKey flow) => _flow = flow;

#pragma warning disable CS0067 // the middleware never raises these; the interface just carries them
        public event Action<FlowKey>? TcpConnectEstablished;
        public event Action<FlowKey>? TcpConnectClosed;
        public event Action<IPAddress, ushort>? UdpBindAdded;
        public event Action<IPAddress, ushort>? UdpBindRemoved;
#pragma warning restore CS0067

        public IReadOnlyCollection<uint> TrackedProcessIds => new[] { Pid };
        public IReadOnlyCollection<FlowKey> TcpSnapshot => new[] { _flow };
        public void Start() { }
        public void AddProcess(uint pid) { }
        public bool RemoveProcess(uint pid) => false;
        public bool IsTrackedProcess(uint pid) => pid == Pid;
        public bool IsTrackedTcp(FlowKey key) => key.Equals(_flow);
        public bool IsTrackedUdp(IPAddress localAddr, ushort localPort) => false;

        public bool TryGetTcpProcessId(FlowKey key, out uint processId)
        {
            processId = key.Equals(_flow) ? Pid : 0;
            return processId != 0;
        }

        public bool TryGetUdpProcessId(IPAddress localAddr, ushort localPort, out uint processId)
        {
            processId = 0;
            return false;
        }

        public bool TryReconcileFromKernel(out int tcpAdded, out int udpAdded, bool force = false)
        {
            tcpAdded = 0;
            udpAdded = 0;
            return false;
        }

        public void Dispose() { }
    }
}
