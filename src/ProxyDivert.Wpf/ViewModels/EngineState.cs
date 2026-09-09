namespace ProxyDivert.Wpf.ViewModels;

/// <summary>
/// What the engine switch is doing, as the window sees it.
/// </summary>
/// <remarks>
/// Three states rather than a bool because switching redirection on is not instant: the driver
/// opens, every running process is enumerated and attached, and the VPN tunnels a filter routes
/// through are dialled. A bool would either show the switch as on while none of that has happened
/// yet, or leave it off while it plainly is being switched — so the in-between is a state of its
/// own, and the switch shows it in a colour that is neither.
/// </remarks>
public enum EngineState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}
