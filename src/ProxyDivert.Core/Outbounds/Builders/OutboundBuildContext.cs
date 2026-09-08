using Microsoft.Extensions.Logging;

namespace ProxyDivert.Core.Outbounds.Builders;

/// <summary>
/// Everything a builder needs that does not come from the outbound itself.
/// </summary>
public sealed class OutboundBuildContext
{
    /// <summary>
    /// The stamp the instance carries, computed by the factory so that every kind is stamped by the
    /// same rule. A builder that worked out its own could disagree with the one its owner compares
    /// against later, and that disagreement is precisely how renaming an outbound came to drop a
    /// tunnel that was working. See <see cref="OutboundSignature"/>.
    /// </summary>
    public required string Signature { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Where wireproxy.exe lives. Null or empty means "look next to this executable, then on PATH",
    /// which is what a user who dropped the binary in the tool's folder expects. One setting for the
    /// machine rather than one per outbound: it is the same binary whichever tunnel it runs.
    /// </summary>
    public string? WireProxyPath { get; init; }
}
