using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Redirect.Enums;

namespace ProxyDivert.Core.Configuration;

// Loads and saves AppConfig as JSON.
//
// Two things it deliberately does NOT do: throw when the file is missing (a first run is normal),
// and leave a half-written file behind (a crash mid-save must not cost the user their whole
// setup — the write goes to a temp file and is then swapped in).
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

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
            DecryptSecrets(config);
            Migrate(config);
            return config;
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

        // Encrypt on a copy: the live objects keep their clear-text passwords, because the
        // outbound factory needs them to build a proxy source.
        AppConfig forDisk = CloneWithEncryptedSecrets(config);
        string json = JsonSerializer.Serialize(forDisk, SerializerOptions);

        string? dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

        string tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        // File.Replace needs an existing destination; on a first save there isn't one.
        if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
        else File.Move(tempPath, FilePath);
    }

    // Brings a file written by an older version up to the current shape. Kept here rather than in
    // the model so AppConfig stays plain data.
    //
    // v1 -> v2: BlockIpv6 became Ipv6 (Redirect / Block / Ignore). "Block it" carries over as-is.
    // "Don't block it" used to mean the target's IPv6 escaped the proxy entirely — that was the
    // only thing the old code could do, not what the setting was asking for, so it becomes Redirect.
    internal static void Migrate(AppConfig config)
    {
        if (config.Version < 2)
            config.Ipv6 = config.BlockIpv6 == true ? Ipv6Mode.Block : Ipv6Mode.Redirect;

        config.BlockIpv6 = null;
        config.Version = AppConfig.CurrentVersion;
    }

    private static void DecryptSecrets(AppConfig config)
    {
        foreach (Outbound outbound in config.Outbounds)
        {
            outbound.Password = SecretProtector.Unprotect(outbound.Password);
            outbound.PreSharedKey = SecretProtector.Unprotect(outbound.PreSharedKey);
        }
    }

    private static AppConfig CloneWithEncryptedSecrets(AppConfig config)
    {
        // Round-tripping through JSON is the cheapest correct deep copy here: the model is plain
        // data, and this runs once per save, not per connection.
        string json = JsonSerializer.Serialize(config, SerializerOptions);
        AppConfig clone = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions)!;
        foreach (Outbound outbound in clone.Outbounds)
        {
            outbound.Password = SecretProtector.Protect(outbound.Password);
            // The IPsec group key opens the tunnel just as a password does, so it gets the same
            // treatment rather than sitting in the JSON in the clear.
            outbound.PreSharedKey = SecretProtector.Protect(outbound.PreSharedKey);
        }
        return clone;
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
