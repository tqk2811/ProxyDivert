using System.Text.Json;
using System.Windows.Threading;
using ProxyDivert.Api.Interfaces;
using ProxyDivert.Api.Models;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Services;
using ProxyDivert.ViewModels;

namespace ProxyDivert.Api;

/// <summary>
/// The API's way into the running window. Every call is marshalled onto the UI thread, which is
/// the thread that owns <see cref="AppServices.Config"/> and every grid built from it: a request is
/// then just one more thing the user could have done with the mouse, in turn with the rest.
/// </summary>
public sealed class WindowApiBackend : IApiBackend
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher;

    public WindowApiBackend(AppServices services, MainViewModel main, Dispatcher dispatcher)
    {
        _services = services;
        _main = main;
        _dispatcher = dispatcher;
    }

    // InvokeAsync stores what the delegate throws in its Task, so a 404 thrown here reaches the request.
    public Task<T> ReadAsync<T>(Func<AppConfig, T> read)
        => _dispatcher.InvokeAsync(() => read(_services.CloneApplied())).Task;

    public Task<T> EditAsync<T>(Func<AppConfig, T> edit)
        => _dispatcher.InvokeAsync(async () =>
        {
            // Saving now would write the user's half-finished edits along with this request, under
            // a Save they never pressed. Refuse instead; the tool can retry once they are dealt with.
            if (_services.HasPendingChanges)
                throw ApiException.Conflict(
                    "PENDING_CHANGES", "The window has unsaved changes; save or discard them first.");

            AppConfig copy = _services.CloneApplied();
            T result = edit(copy);
            copy.Normalize();

            // Only the routing lists: outbounds are not the API's to change yet, and theirs carry
            // KeepConnected, which the tunnels flip on their own.
            _services.Config.Policies = copy.Policies;
            _services.Config.ProcessRules = copy.ProcessRules;
            await _main.SaveFromApiAsync();

            // The objects the request built are live in the window from here on; what goes back
            // over the wire is a copy the grids cannot change while it is being written out.
            return Copy(result);
        }).Task.Unwrap();

    public Task<ApiStatus> GetStatusAsync()
        => _dispatcher.InvokeAsync(() => new ApiStatus
        {
            EngineRunning = _services.Engine.IsRunning,
            HasPendingChanges = _services.HasPendingChanges,
            Vpns = _services.Vpn.Statuses
                .Select(s => new ApiVpnStatus
                {
                    OutboundId = s.OutboundId,
                    OutboundName = s.OutboundName,
                    State = s.State,
                    Error = s.Error,
                })
                .ToList(),
        }).Task;

    public Task<bool> SetEngineRunningAsync(bool running)
        => _dispatcher.InvokeAsync(async () =>
        {
            bool now = await _main.SetEngineRunningAsync(running);
            if (running && !now)
                throw new ApiException(500, "ENGINE_START_FAILED", _main.StatusMessage ?? "Redirection did not start.");
            return now;
        }).Task.Unwrap();

    public Task SetVpnConnectedAsync(Guid outboundId, bool connected)
        => _dispatcher.InvokeAsync(async () =>
        {
            Outbound outbound = _services.Config.Outbounds.FirstOrDefault(o => o.Id == outboundId)
                ?? throw ApiException.NotFound("OUTBOUND_NOT_FOUND", $"No outbound {outboundId}.");
            if (!_services.Vpn.CanKeep(outbound) || !outbound.IsEnabled)
                throw ApiException.BadRequest("NOT_A_VPN", "This outbound is not an enabled tunnel that can be held up.");

            // The same rule as the Disconnect button: a tunnel a live filter routes through stays up.
            if (!connected && _services.Engine.IsRunning
                && OutboundUsage.RoutedOutboundIds(_services.Config).Contains(outboundId))
                throw ApiException.Conflict(
                    "VPN_IN_USE", "Redirection is on and a filter routes through this tunnel.");

            await _services.SetVpnConnectedAsync(outbound, connected);
        }).Task.Unwrap();

    private static T Copy<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
