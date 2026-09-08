using System;
using System.Globalization;
using Xunit;
using AppDurationConverter = ProxyDivert.Wpf.Converters.DurationConverter;

namespace ProxyDivert.Core.Tests;

// A connection that has ended has a length; one still running has an age. The column shows both,
// and the difference between them is the whole point: a finished row used to keep counting.
public class DurationConverterTests
{
    private static string Convert(DateTime started, DateTime? ended)
        => (string)new AppDurationConverter().Convert(
            new object?[] { started, ended }, typeof(string), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void A_finished_connection_shows_how_long_it_lasted_and_stops_there()
    {
        DateTime started = DateTime.UtcNow.AddMinutes(-20);

        string first = Convert(started, started.AddSeconds(3.2));
        string later = Convert(started, started.AddSeconds(3.2));

        Assert.Equal("3.2s", first);
        Assert.Equal(first, later);
    }

    [Fact]
    public void A_running_connection_is_measured_up_to_now()
    {
        string shown = Convert(DateTime.UtcNow.AddSeconds(-90), ended: null);

        Assert.StartsWith("1m 3", shown, StringComparison.Ordinal);
    }

    // A clock that has stepped backwards, or a row read the instant it was created, must not
    // produce a negative duration in the cell.
    [Fact]
    public void An_end_before_the_start_reads_as_zero()
    {
        DateTime started = DateTime.UtcNow;

        Assert.Equal("0.0s", Convert(started, started.AddSeconds(-5)));
    }
}
