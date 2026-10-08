using System;
using System.Runtime;
using System.Threading;

namespace ProxyDivert.Core.Hosting;

/// <summary>
/// Runtime settings that trade throughput for shorter pauses, switched on only while redirection
/// runs: every packet of the machine then waits on this process, so a GC pause or a work item
/// queued behind a starved thread pool is lag in someone's game.
/// </summary>
/// <remarks>
/// <para>
/// SustainedLowLatency keeps the background GC from turning into blocking full collections while it
/// can avoid it; the cost is a larger heap, which only matters while the engine is up.
/// </para>
/// <para>
/// The thread pool grows by about one thread a second past its minimum, and every relayed
/// connection, held SYN and proxy handshake goes through it. A burst (a browser opening dozens of
/// connections, SSH.NET blocking pool threads) used to wait out that injection rate. Raising the
/// minimum lets the pool hand out threads at once up to that count; idle ones still retire.
/// </para>
/// Not thread-safe: called from the session's work queue, which runs one thing at a time.
/// </remarks>
internal static class RuntimeLatencyTuning
{
    private const int MinWorkerThreadsFloor = 32;

    private static bool _applied;
    private static GCLatencyMode _previousLatencyMode;
    private static int _previousMinWorkers;
    private static int _previousMinIo;

    public static void Apply()
    {
        if (_applied) return;
        _previousLatencyMode = GCSettings.LatencyMode;
        ThreadPool.GetMinThreads(out _previousMinWorkers, out _previousMinIo);

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        int workers = Math.Max(_previousMinWorkers, Math.Max(MinWorkerThreadsFloor, Environment.ProcessorCount * 4));
        ThreadPool.SetMinThreads(workers, _previousMinIo);
        _applied = true;
    }

    public static void Restore()
    {
        if (!_applied) return;
        GCSettings.LatencyMode = _previousLatencyMode;
        ThreadPool.SetMinThreads(_previousMinWorkers, _previousMinIo);
        _applied = false;
    }
}
