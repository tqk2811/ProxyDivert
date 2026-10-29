using System;
using System.IO;
using ProxyDivert.Core.Configuration;
using TqkLibrary.WinDivert.Pipeline.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

public class PumpLatencyAlertStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ProxyDivertTests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "latency.json");

    public PumpLatencyAlertStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void A_missing_file_is_written_with_the_defaults()
    {
        PumpLatencyAlertOptions options = new PumpLatencyAlertStore(FilePath).LoadOrCreate();

        Assert.Equal(1_000, options.InformationThresholdMicroseconds);
        Assert.Equal(10_000, options.WarningThresholdMicroseconds);
        string json = File.ReadAllText(FilePath);
        Assert.Contains("\"InformationThresholdMicroseconds\": 1000", json);
        Assert.Contains("\"WarningThresholdMicroseconds\": 10000", json);
        Assert.DoesNotContain("IsEnabled", json);
    }

    [Fact]
    public void An_edited_file_is_read_back()
    {
        File.WriteAllText(FilePath, "{ \"InformationThresholdMicroseconds\": 500, \"WarningThresholdMicroseconds\": 0 }");

        PumpLatencyAlertOptions options = new PumpLatencyAlertStore(FilePath).LoadOrCreate();

        Assert.Equal(500, options.InformationThresholdMicroseconds);
        Assert.Equal(0, options.WarningThresholdMicroseconds);
    }

    [Fact]
    public void A_broken_file_falls_back_to_the_defaults_and_is_left_alone()
    {
        const string broken = "{ \"InformationThresholdMicroseconds\": ";
        File.WriteAllText(FilePath, broken);

        PumpLatencyAlertOptions options = new PumpLatencyAlertStore(FilePath).LoadOrCreate();

        Assert.Equal(1_000, options.InformationThresholdMicroseconds);
        Assert.Equal(broken, File.ReadAllText(FilePath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
