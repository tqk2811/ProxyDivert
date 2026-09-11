using System;
using ProxyDivert.Core.Engine.Interfaces;
using ProxyDivert.Core.Processes.Enums;

namespace ProxyDivert.Core.Engine.Extensions;

public static class ProcessDetectionModeExtensions
{
    /// <summary>What a saved detection setting means, as the object that carries it out.</summary>
    /// <remarks>
    /// The one switch on the setting left in the application. The configuration file keeps the enum
    /// — it is what the settings page and the file both speak — and everything that acts on it asks
    /// here, so a new mode is a class and one line below.
    /// </remarks>
    public static IProcessDetectionStrategy Strategy(this ProcessDetectionMode mode) => mode switch
    {
        ProcessDetectionMode.ProcessEvents => ProcessEventDetection.Instance,
        ProcessDetectionMode.NetworkSniff => SocketSniffDetection.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "No detection strategy for this mode"),
    };
}
