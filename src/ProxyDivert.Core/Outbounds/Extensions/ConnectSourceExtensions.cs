using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.Proxy.StreamHelpers;

namespace ProxyDivert.Core.Outbounds.Extensions;

// TqkLibrary.Proxy exposes an established tunnel as a raw Stream, leaving every caller to repeat
// the same "pump both directions until one side disconnects" boilerplate. This is that step.
public static class ConnectSourceExtensions
{
    /// <param name="shutdownClientSend">
    /// How to tell the process that nothing more is coming from the far side. The client stream is
    /// a decorator over the relay's socket — it counts bytes and replays the peeked header — so the
    /// transfer helper cannot find the socket to half-close on its own, and without this a server
    /// that finished speaking left the process waiting on a response that had already ended.
    /// </param>
    public static async Task ForwardAsync(
        this IConnectSource source,
        Stream clientStream,
        Guid tunnelId,
        ILoggerFactory? loggerFactory = null,
        string clientName = "process",
        string proxyName = "proxy",
        Action? shutdownClientSend = null,
        CancellationToken cancellationToken = default)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (clientStream is null) throw new ArgumentNullException(nameof(clientStream));

        Stream proxyStream = await source.GetStreamAsync(cancellationToken).ConfigureAwait(false);
        await new StreamTransferHelper(clientStream, proxyStream, tunnelId, loggerFactory)
            .DebugName(clientName, proxyName)
            .ShutdownSendWith(shutdownClientSend, null)
            .WaitUntilDisconnect(cancellationToken)
            .ConfigureAwait(false);
    }
}
