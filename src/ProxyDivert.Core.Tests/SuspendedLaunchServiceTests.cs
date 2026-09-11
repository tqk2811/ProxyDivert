using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Hosting;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;
using Xunit;
using static ProxyDivert.Core.Tests.SessionHarness;

namespace ProxyDivert.Core.Tests;

// Launching frozen exists for one guarantee: the program does not run a single instruction before
// the driver has it. These hold the service to that — and to the half that used to be missing, that
// a program whose redirect could not be put in place is ended rather than let run past it (A11).
public class SuspendedLaunchServiceTests
{
    private const string AppPath = @"C:\Programs\app.exe";

    [Fact]
    public async Task Named_by_its_id_a_program_runs_only_once_the_driver_has_it()
    {
        var launcher = new FakeLauncher();
        await using var harness = Harness(launcher);
        AppConfig config = Config();
        await harness.Session.StartAsync(config);

        bool inDriverWhenResumed = false;
        launcher.OnResume = pid => inDriverWhenResumed = harness.Redirector.IsTrackedProcessId(pid);

        uint pid = await Service(harness).LaunchUnderPolicyAsync(AppPath, null, config.Policies[0].Id);

        Assert.Equal(launcher.Launched.Single().Pid, pid);
        Assert.True(inDriverWhenResumed);
    }

    // The window's way: a filter for the program, saved, and only then a scan. The scan is what
    // meets the program, so it must meet the filter too.
    [Fact]
    public async Task Through_a_filter_the_new_filter_is_in_force_before_the_program_runs()
    {
        var launcher = new FakeLauncher();
        await using var harness = Harness(launcher);
        AppConfig config = Config();
        await harness.Session.StartAsync(config);
        harness.Machine.Start(FakeLauncher.FirstPid, "app.exe");

        bool inDriverWhenResumed = false;
        launcher.OnResume = pid => inDriverWhenResumed = harness.Redirector.IsTrackedProcessId(pid);

        await Service(harness).LaunchUnderFiltersAsync(AppPath, null, () =>
        {
            config.ProcessRules.Add(Filter("app.exe", config.Policies[0].Id));
            return harness.Session.ApplyAsync(config);
        });

        Assert.True(inDriverWhenResumed);
    }

    // A11, end to end: the save fails, and the program it was for must not be resumed with the
    // filter nowhere. Disposing a process launched frozen and never resumed is what ends it.
    [Fact]
    public async Task Through_a_filter_that_could_not_be_saved_the_program_is_ended_not_run()
    {
        string blocked = Path.Combine(Path.GetTempPath(), $"proxydivert-launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(blocked);
        try
        {
            var launcher = new FakeLauncher();
            await using var harness = Harness(launcher, new ConfigStore(blocked));
            AppConfig config = Config();
            await harness.Session.StartAsync(config);

            await Assert.ThrowsAsync<IOException>(() => Service(harness).LaunchUnderFiltersAsync(AppPath, null, () =>
            {
                config.ProcessRules.Add(Filter("app.exe", config.Policies[0].Id));
                return harness.Session.ApplyAsync(config);
            }));

            FakeSuspendedProcess launched = launcher.Launched.Single();
            Assert.False(launched.Resumed);
            Assert.True(launched.Disposed);
        }
        finally
        {
            Directory.Delete(blocked, recursive: true);
        }
    }

    // With redirection off there is no engine to name the pid to, and running the program bare is
    // exactly what launching it frozen was meant to prevent.
    [Fact]
    public async Task Named_by_its_id_with_redirection_off_the_program_is_ended_not_run()
    {
        var launcher = new FakeLauncher();
        await using var harness = Harness(launcher);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(harness).LaunchUnderPolicyAsync(AppPath, null, Guid.NewGuid()));

        FakeSuspendedProcess launched = launcher.Launched.Single();
        Assert.False(launched.Resumed);
        Assert.True(launched.Disposed);
    }

    [Fact]
    public async Task A_step_that_throws_before_it_even_starts_still_ends_the_program()
    {
        var launcher = new FakeLauncher();
        await using var harness = Harness(launcher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(harness)
            .LaunchAsync(AppPath, null, _ => throw new InvalidOperationException("no filter for it")));

        Assert.False(launcher.Launched.Single().Resumed);
        Assert.True(launcher.Launched.Single().Disposed);
    }

    private static SessionHarness Harness(FakeLauncher launcher, ConfigStore? store = null)
        => new SessionHarness(store, services => services.AddSingleton<ISuspendedProcessLauncher>(launcher));

    private static SuspendedLaunchService Service(SessionHarness harness)
        => harness.Services.GetRequiredService<SuspendedLaunchService>();

    // Nothing is started; the "process" only records what was done to it.
    private sealed class FakeLauncher : ISuspendedProcessLauncher
    {
        public const uint FirstPid = 500;
        private uint _nextPid = FirstPid;

        public List<FakeSuspendedProcess> Launched { get; } = new List<FakeSuspendedProcess>();

        /// <summary>Run at the moment a process is resumed — what it would meet as it starts running.</summary>
        public Action<uint>? OnResume { get; set; }

        public ISuspendedProcess Launch(string exePath, string? args)
        {
            var process = new FakeSuspendedProcess(_nextPid++, pid => OnResume?.Invoke(pid));
            Launched.Add(process);
            return process;
        }

        public ISuspendedProcess AttachSuspend(uint pid) => throw new NotSupportedException();
    }

    private sealed class FakeSuspendedProcess : ISuspendedProcess
    {
        private readonly Action<uint> _onResume;

        public FakeSuspendedProcess(uint pid, Action<uint> onResume)
        {
            Pid = pid;
            _onResume = onResume;
        }

        public uint Pid { get; }
        public bool Resumed { get; private set; }
        public bool Disposed { get; private set; }

        public void Resume()
        {
            _onResume(Pid);
            Resumed = true;
        }

        public void Dispose() => Disposed = true;
    }
}
