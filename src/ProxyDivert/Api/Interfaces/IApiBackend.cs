using ProxyDivert.Api.Models;
using ProxyDivert.Core.Configuration.Models;

namespace ProxyDivert.Api.Interfaces;

/// <summary>
/// What the HTTP endpoints need from the running application. The window implements it; tests
/// hand the host a fake over a plain <see cref="AppConfig"/>.
/// </summary>
public interface IApiBackend
{
    /// <summary>
    /// Runs <paramref name="read"/> against a private copy of the configuration as last saved —
    /// not the window's edits in progress — so the result may be kept and serialised freely.
    /// </summary>
    Task<T> ReadAsync<T>(Func<AppConfig, T> read);

    /// <summary>
    /// Runs <paramref name="edit"/> against a copy of the configuration and, if it returns without
    /// throwing, applies the copy the way the Save button does. Throws a 409
    /// <see cref="ApiException"/> while the window holds edits that are not saved yet, rather than
    /// saving them along with this one.
    /// </summary>
    Task<T> EditAsync<T>(Func<AppConfig, T> edit);

    Task<ApiStatus> GetStatusAsync();

    /// <summary>Switches redirection on or off; returns whether it is running afterwards.</summary>
    Task<bool> SetEngineRunningAsync(bool running);

    Task SetVpnConnectedAsync(Guid outboundId, bool connected);
}
