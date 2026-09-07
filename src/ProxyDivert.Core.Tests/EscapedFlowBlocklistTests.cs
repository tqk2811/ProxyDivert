using System.Net;
using TqkLibrary.WinDivert.Flow.Models;
using TqkLibrary.WinDivert.Redirect;
using Xunit;

namespace ProxyDivert.Core.Tests;

public class EscapedFlowBlocklistTests
{
    private static FlowKey Flow(int n)
        => new FlowKey(6, IPAddress.Parse("10.0.0.5"), (ushort)(50000 + n), IPAddress.Parse("93.184.216.34"), 443);

    [Fact]
    public void Add_is_once_per_flow_and_remove_forgets_it()
    {
        var list = new EscapedFlowBlocklist();

        Assert.True(list.Add(Flow(1)));
        Assert.False(list.Add(Flow(1)));
        Assert.True(list.Contains(Flow(1)));
        Assert.False(list.Contains(Flow(2)));

        Assert.True(list.Remove(Flow(1)));
        Assert.False(list.Contains(Flow(1)));
        Assert.Equal(0, list.Count);
    }

    // Bounded: past the capacity the set starts over rather than growing for the life of the run.
    [Fact]
    public void Past_capacity_the_list_starts_over()
    {
        var list = new EscapedFlowBlocklist(capacity: 2);
        list.Add(Flow(1));
        list.Add(Flow(2));

        list.Add(Flow(3));

        Assert.Equal(1, list.Count);
        Assert.True(list.Contains(Flow(3)));
        Assert.False(list.Contains(Flow(1)));
    }
}
