using System;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Redirecting the tool itself sends every connection the proxy makes straight back into the relay
// that made it. The loop is not obvious from a log — it looks like the network hanging — so it is
// worth a test that the refusal stays in place.
public class ProcessWatcherSelfExclusionTests
{
    [Fact]
    public void The_tool_refuses_to_redirect_itself()
    {
        using var watcher = new ProcessWatcher(NullLogger<ProcessWatcher>.Instance);

        TrackedProcess? tracked = watcher.AttachProcessId((uint)Environment.ProcessId, Guid.NewGuid());

        Assert.Null(tracked);
        Assert.Empty(watcher.Tracked);
    }

    [Fact]
    public void Another_process_is_still_attachable()
    {
        // Pid 4 is the Windows System process: it always exists, and nothing here actually touches
        // its traffic — Attach only records the intent.
        using var watcher = new ProcessWatcher(NullLogger<ProcessWatcher>.Instance);

        TrackedProcess? tracked = watcher.AttachProcessId(4, Guid.NewGuid());

        Assert.NotNull(tracked);
        Assert.True(tracked!.IsExplicit);
    }
}
