using System;
using System.IO;
using System.Text.Json;
using TqkLibrary.WinDivert.Pipeline.Models;

namespace ProxyDivert.Core.Configuration;

// The thresholds at which the packet pump reports a slow window below Debug, in a file of their own
// beside the executable. Not in AppConfig: nothing in the window edits them, and a tuning knob
// someone opens in a text editor is easier to find on its own than buried in the rule list.
//
// A missing file is written with the defaults, so the knob is there to be found. A file that does
// not parse is left alone — it is the user's, half-edited perhaps — and the defaults are used.
public sealed class PumpLatencyAlertStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    public string FilePath { get; }

    public PumpLatencyAlertStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultFilePath();
    }

    public static string DefaultFilePath()
        => Path.Combine(AppContext.BaseDirectory, "proxydivert.latency.json");

    public PumpLatencyAlertOptions LoadOrCreate()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = new PumpLatencyAlertOptions();
            // Best effort: a read-only folder costs the file, not the alerts.
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, SerializerOptions)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return defaults;
        }

        try
        {
            return JsonSerializer.Deserialize<PumpLatencyAlertOptions>(File.ReadAllText(FilePath), SerializerOptions)
                ?? new PumpLatencyAlertOptions();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PumpLatencyAlertOptions();
        }
    }
}
