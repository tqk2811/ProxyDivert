using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Engine;
using Xunit;

namespace ProxyDivert.Core.Tests;

// When a run of failed DoH lookups becomes a log line, and when it stops being one.
public class DohHealthTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public int Count(LogLevel level) => Entries.Count(e => e.Level == level);
    }

    private sealed class Clock
    {
        public long Ticks = 1000;

        public void Advance(TimeSpan span) => Ticks += (long)(span.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    }

    private static DohHealth Create(CapturingLogger logger, Clock clock)
        => new DohHealth("out", logger, failureThreshold: 3, TimeSpan.FromSeconds(30), () => clock.Ticks);

    [Fact]
    public void One_or_two_failures_log_nothing()
    {
        var logger = new CapturingLogger();
        DohHealth health = Create(logger, new Clock());

        health.OnFailure("x");
        health.OnFailure("x");

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Third_failure_in_a_row_warns_once()
    {
        var logger = new CapturingLogger();
        DohHealth health = Create(logger, new Clock());

        for (int i = 0; i < 4; i++) health.OnFailure("timeout");

        Assert.Equal(1, logger.Count(LogLevel.Warning));
        Assert.Contains("failing", logger.Entries[0].Message);
        Assert.Contains("timeout", logger.Entries[0].Message);
    }

    [Fact]
    public void A_success_between_failures_restarts_the_count()
    {
        var logger = new CapturingLogger();
        DohHealth health = Create(logger, new Clock());

        health.OnFailure("x");
        health.OnFailure("x");
        health.OnSuccess();
        health.OnFailure("x");
        health.OnFailure("x");

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Success_after_a_reported_outage_logs_recovery_with_the_count_once()
    {
        var logger = new CapturingLogger();
        DohHealth health = Create(logger, new Clock());
        for (int i = 0; i < 5; i++) health.OnFailure("x");

        health.OnSuccess();
        health.OnSuccess();

        Assert.Equal(1, logger.Count(LogLevel.Information));
        Assert.Contains("recovered after 5", logger.Entries.Single(e => e.Level == LogLevel.Information).Message);
    }

    [Fact]
    public void Success_without_a_reported_failure_logs_nothing()
    {
        var logger = new CapturingLogger();
        DohHealth health = Create(logger, new Clock());

        health.OnSuccess();
        health.OnFailure("x");
        health.OnSuccess();

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void A_second_outage_is_reported_again_after_recovery()
    {
        var logger = new CapturingLogger();
        DohHealth health = Create(logger, new Clock());
        for (int i = 0; i < 3; i++) health.OnFailure("x");
        health.OnSuccess();

        for (int i = 0; i < 3; i++) health.OnFailure("y");

        Assert.Equal(2, logger.Count(LogLevel.Warning));
    }

    [Fact]
    public void Reminder_comes_at_most_once_per_interval_while_reported()
    {
        var logger = new CapturingLogger();
        var clock = new Clock();
        DohHealth health = Create(logger, clock);
        for (int i = 0; i < 3; i++) health.OnFailure("x");
        Assert.Equal(1, logger.Count(LogLevel.Warning));

        clock.Advance(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 20; i++) health.OnFailure("x");
        Assert.Equal(1, logger.Count(LogLevel.Warning));

        clock.Advance(TimeSpan.FromSeconds(21));
        health.OnFailure("x");
        health.OnFailure("x");
        Assert.Equal(2, logger.Count(LogLevel.Warning));
        Assert.Contains("still failing", logger.Entries.Last().Message);
    }
}
