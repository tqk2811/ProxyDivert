namespace ProxyDivert.Api.Models;

public sealed class ApiStatus
{
    public required bool EngineRunning { get; init; }

    /// <summary>True while the window holds edits not saved yet; every write answers 409 until then.</summary>
    public required bool HasPendingChanges { get; init; }

    public required IReadOnlyList<ApiVpnStatus> Vpns { get; init; }
}
