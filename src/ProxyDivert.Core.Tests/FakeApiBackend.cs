using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProxyDivert.Api;
using ProxyDivert.Api.Interfaces;
using ProxyDivert.Api.Models;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;

namespace ProxyDivert.Core.Tests;

/// <summary>
/// The window's backend without the window: the same copy-edit-apply contract over a plain
/// <see cref="AppConfig"/>, counting how many times it "saved".
/// </summary>
internal sealed class FakeApiBackend : IApiBackend
{
    public AppConfig Config { get; private set; } = AppConfig.CreateDefault();

    public bool HasPendingChanges { get; set; }

    public int Saves { get; private set; }

    public bool EngineRunning { get; private set; }

    public List<(Guid Id, bool Connected)> VpnCalls { get; } = new();

    public Task<T> ReadAsync<T>(Func<AppConfig, T> read) => Task.FromResult(read(ConfigStore.Clone(Config)));

    public Task<T> EditAsync<T>(Func<AppConfig, T> edit)
    {
        if (HasPendingChanges)
            throw ApiException.Conflict("PENDING_CHANGES", "unsaved");
        AppConfig copy = ConfigStore.Clone(Config);
        T result = edit(copy);
        Config = copy.Normalize();
        Saves++;
        return Task.FromResult(result);
    }

    public Task<ApiStatus> GetStatusAsync() => Task.FromResult(new ApiStatus
    {
        EngineRunning = EngineRunning,
        HasPendingChanges = HasPendingChanges,
        Vpns = Array.Empty<ApiVpnStatus>(),
    });

    public Task<bool> SetEngineRunningAsync(bool running)
    {
        EngineRunning = running;
        return Task.FromResult(running);
    }

    public Task SetVpnConnectedAsync(Guid outboundId, bool connected)
    {
        VpnCalls.Add((outboundId, connected));
        return Task.CompletedTask;
    }
}
