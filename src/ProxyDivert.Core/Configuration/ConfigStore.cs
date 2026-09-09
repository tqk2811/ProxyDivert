using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxyDivert.Core.Configuration.Models;

namespace ProxyDivert.Core.Configuration;

// Loads and saves AppConfig as JSON.
//
// Two things it deliberately does NOT do: throw when the file is missing (a first run is normal),
// and leave a half-written file behind (a crash mid-save must not cost the user their whole
// setup — the write goes to a temp file and is then swapped in).
//
// Passwords and pre-shared keys are written exactly as they were typed. They used to go through
// DPAPI on the way out, which tied the file to one Windows account and made a config copied to
// another machine come back with the passwords silently blank; the file is now plainly readable
// and plainly editable, and protecting it is left to the folder it sits in.
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // One save at a time. The window saves on the UI thread and the engine's worker queue saves
    // after applying a configuration, and both write the same file: two at once meant one writing
    // its temp file while the other replaced the config with it half-finished. Static, because the
    // two callers hold different ConfigStore instances over the same path.
    private static readonly object SaveLock = new object();

    public string FilePath { get; }

    public ConfigStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultFilePath();
    }

    // Next to the executable, matching how the tool is distributed (a folder you can move).
    public static string DefaultFilePath()
        => Path.Combine(AppContext.BaseDirectory, "proxydivert.config.json");

    // Returns the stored configuration, or a fresh default when there is nothing usable on disk.
    // A corrupted file is kept aside as .bak rather than deleted — it may contain a long rule list
    // the user would rather repair by hand than retype.
    public AppConfig Load()
    {
        if (!File.Exists(FilePath)) return AppConfig.CreateDefault();

        try
        {
            string json = File.ReadAllText(FilePath);
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            if (config == null) return AppConfig.CreateDefault();

            // The file is plain JSON and the user is invited to edit it, so what comes back may
            // reference a policy that is not there any more. AppConfig knows what a whole
            // configuration looks like; this only has to ask.
            return config.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            TryBackupCorruptFile();
            return AppConfig.CreateDefault();
        }
    }

    public void Save(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        // Serialised straight from the caller's instance: nothing is rewritten on the way out, so
        // there is no copy to make. The objects handed in are left untouched, which is what the
        // outbound factory relies on — it keeps using them to build proxy sources.
        string json = JsonSerializer.Serialize(config, SerializerOptions);

        string? dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

        // A name of its own per save. The lock below only covers this process, and a fixed
        // ".tmp" meant a second writer — another copy of the tool over the same folder — could
        // truncate the file this one had just filled, leaving Replace to swap in a cut-off
        // config. There is no backup of a good file, only a .bak of one that failed to load.
        string tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";

        lock (SaveLock)
        {
            try
            {
                File.WriteAllText(tempPath, json);
                // File.Replace needs an existing destination; on a first save there isn't one.
                if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
                else File.Move(tempPath, FilePath);
            }
            catch
            {
                // A temp file that never became the config is litter sitting next to it.
                try { File.Delete(tempPath); } catch { }
                throw;
            }
        }
    }

    /// <summary>
    /// A deep copy that shares nothing with <paramref name="config"/> — not a list, not a rule.
    /// </summary>
    /// <remarks>
    /// This is what the engine runs on. The configuration has three layers: the instance the view
    /// models edit, the snapshot the engine holds, and the file. Handing the engine the edited
    /// instance itself meant a rule added in the grid was already being matched against new
    /// processes before the user pressed Save, on a list the watcher was enumerating from another
    /// thread. A snapshot taken at Save is the whole edit or none of it.
    ///
    /// Round-tripping through JSON is the cheapest correct deep copy here: the model is plain
    /// data, and this runs once per save, not per connection.
    ///
    /// The copy is normalised, the original is not. This is the last point before the engine, and
    /// the engine should never have to reason about a reference that goes nowhere; the instance the
    /// window is editing stays exactly as the user left it, because repairing it under them mid-edit
    /// would move rows they are looking at.
    /// </remarks>
    public static AppConfig Clone(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        string json = JsonSerializer.Serialize(config, SerializerOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions)!.Normalize();
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            string backup = FilePath + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(FilePath, backup);
        }
        catch
        {
            // Best effort: a locked or unreadable file simply stays where it is.
        }
    }
}
