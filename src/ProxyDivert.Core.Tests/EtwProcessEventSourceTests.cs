using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The ETW source against the real provider: there is no way to fake a kernel trace session, and a
// test that only checked the plumbing would not have caught the thing worth checking — whether the
// payload field names in the manifest are what the code reads.
//
// Opening a trace session needs elevation, so an unelevated run reports success without asserting
// anything. That is deliberate: the suite is expected to run either way, and a test that failed on
// a developer machine for lack of a UAC prompt would just be turned off.
public class EtwProcessEventSourceTests
{
    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void AStartedProcess_IsReported_WithItsPidParentAndName()
    {
        if (!IsElevated()) return;

        using var source = new EtwProcessEventSource(NullLogger.Instance);
        var seen = new ManualResetEventSlim();
        ProcessStartedEvent started = default;

        // cmd.exe is started below; anything else on the machine may also be reported, so the
        // handler waits for the pid this test owns rather than the first event to arrive.
        int wantedPid = 0;
        source.Started += e =>
        {
            if (e.ProcessId != (uint)Volatile.Read(ref wantedPid)) return;
            started = e;
            seen.Set();
        };

        Assert.True(source.TryStart(), "the ETW session did not start even though the test is elevated");

        // Enabling a provider is not instant: the session has to be told before the process is
        // created, or its start event is simply not in the stream.
        Thread.Sleep(500);

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        Volatile.Write(ref wantedPid, child.Id);
        child.WaitForExit();

        Assert.True(seen.Wait(TimeSpan.FromSeconds(10)), "no ETW start event arrived for the child process");
        Assert.Equal((uint)child.Id, started.ProcessId);
        Assert.Equal((uint)Environment.ProcessId, started.ParentProcessId);
        // The file name, not the NT device path the provider actually carries.
        Assert.Equal("cmd.exe", started.ImageName, ignoreCase: true);
    }
}
