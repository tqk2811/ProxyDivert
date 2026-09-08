using System;
using System.Net;
using TqkLibrary.WinDivert.SecureDns;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The IP -> domain table a routing rule falls back to when a connection carries no SNI. It is
// written from the DNS pump thread, once per answer, so what it does when it is full matters as
// much as what it remembers.
public class ReverseDnsTableTests
{
    private static IPAddress Ip(int i) => new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i });

    [Fact]
    public void A_name_is_resolvable_for_the_address_it_was_learned_for()
    {
        var table = new ReverseDnsTable();

        table.Add(IPAddress.Parse("93.184.216.34"), "example.com", TimeSpan.FromMinutes(5));

        Assert.Equal("example.com", table.Resolve(IPAddress.Parse("93.184.216.34")));
        Assert.True(table.IsFresh(IPAddress.Parse("93.184.216.34")));
    }

    // Trimming to exactly the capacity left the table full, so the next answer trimmed again: a
    // copy of every entry and an O(n log n) sort per DNS reply, for as long as the tool ran. A
    // batch comes out instead, and the count stays under the cap either way.
    [Fact]
    public void Filling_the_table_leaves_room_so_the_next_answer_does_not_trim_again()
    {
        const int capacity = 64;
        var table = new ReverseDnsTable(capacity: capacity);

        for (int i = 0; i < capacity + 1; i++)
            table.Add(Ip(i), $"host{i}.example.com", TimeSpan.FromMinutes(i + 1));

        Assert.True(table.Count < capacity, $"the table is still full at {table.Count}");

        int afterTrim = table.Count;
        table.Add(Ip(1000), "one-more.example.com", TimeSpan.FromMinutes(5));

        // Room was left, so this one was simply stored.
        Assert.Equal(afterTrim + 1, table.Count);
        Assert.Equal("one-more.example.com", table.Resolve(Ip(1000)));
    }

    [Fact]
    public void The_cap_still_holds_over_a_long_run()
    {
        const int capacity = 64;
        var table = new ReverseDnsTable(capacity: capacity);

        for (int i = 0; i < capacity * 20; i++)
            table.Add(Ip(i), $"host{i}.example.com", TimeSpan.FromMinutes((i % 90) + 1));

        Assert.True(table.Count <= capacity, $"the table grew past its cap to {table.Count}");

        // What survives is what expires last, not what arrived last — a trim drops the entries
        // closest to expiry, so a long-lived mapping outlives a batch of short ones.
        table.Add(Ip(999_999), "long-lived.example.com", TimeSpan.FromHours(24));
        for (int i = 0; i < capacity * 2; i++)
            table.Add(Ip(i), $"host{i}.example.com", TimeSpan.FromMinutes(1));

        Assert.Equal("long-lived.example.com", table.Resolve(Ip(999_999)));
    }
}
