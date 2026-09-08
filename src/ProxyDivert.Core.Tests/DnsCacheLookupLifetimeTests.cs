using System;
using Microsoft.Extensions.DependencyInjection;
using TqkLibrary.WinDivert.DependencyInjection;
using TqkLibrary.WinDivert.Flow.Interfaces;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Who owns the DNS cache lookup.
//
// It holds a polling task and a map that only ever grows, and it belongs to one redirect session:
// start the engine, stop it, and that instance is finished with. Registered as a transient
// IDisposable it was resolved from the ROOT provider, which adds every disposable it creates to a
// list it keeps until the application exits — so each start/stop cycle left another lookup, and
// another map, alive for the rest of the session.
public class DnsCacheLookupLifetimeTests
{
    [Fact]
    public void Each_request_gets_a_lookup_of_its_own()
    {
        using ServiceProvider provider = new ServiceCollection().AddWinDivert().BuildServiceProvider();
        var factory = provider.GetRequiredService<Func<IDnsCacheLookup>>();

        using IDnsCacheLookup first = factory();
        using IDnsCacheLookup second = factory();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void A_lookup_the_caller_has_disposed_is_not_kept_alive_by_the_container()
    {
        using ServiceProvider provider = new ServiceCollection().AddWinDivert().BuildServiceProvider();
        var factory = provider.GetRequiredService<Func<IDnsCacheLookup>>();

        WeakReference weak = CreateAndDispose(factory);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.IsAlive, "the container is still holding a lookup its caller has disposed");
    }

    // In a method of its own so the local holding the instance is out of scope — and therefore out
    // of the GC's reach — by the time the collection runs.
    private static WeakReference CreateAndDispose(Func<IDnsCacheLookup> factory)
    {
        IDnsCacheLookup lookup = factory();
        var weak = new WeakReference(lookup);
        lookup.Dispose();
        return weak;
    }
}
