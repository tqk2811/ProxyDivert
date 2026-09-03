using System;
using System.IO;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ProxyDivertTests", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_directory, "config.json");

    public ConfigStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Missing_file_yields_a_usable_default()
    {
        AppConfig config = new ConfigStore(ConfigPath).Load();

        Assert.NotEmpty(config.Policies);
        Assert.Contains(config.Outbounds, o => o.Kind == OutboundKind.Direct);
    }

    [Fact]
    public void Round_trips_rules_and_policies()
    {
        var store = new ConfigStore(ConfigPath);
        AppConfig config = AppConfig.CreateDefault();
        Guid policyId = config.Policies[0].Id;
        config.Policies[0].Rules.Add(new RoutingRule
        {
            Id = Guid.NewGuid(),
            Matcher = HostMatcherType.Wildcard,
            Pattern = "*.google.com",
            OutboundId = Outbound.BlockId,
            Order = 5,
        });
        config.ProcessRules.Add(new ProcessRule
        {
            Id = Guid.NewGuid(),
            Matcher = ProcessMatcherType.ExeName,
            Pattern = "chrome.exe",
            PolicyId = policyId,
        });

        store.Save(config);
        AppConfig loaded = new ConfigStore(ConfigPath).Load();

        RoutingRule rule = Assert.Single(loaded.Policies[0].Rules);
        Assert.Equal(HostMatcherType.Wildcard, rule.Matcher);
        Assert.Equal("*.google.com", rule.Pattern);
        Assert.Equal(5, rule.Order);
        Assert.Equal("chrome.exe", Assert.Single(loaded.ProcessRules).Pattern);
    }

    [Fact]
    public void Password_is_not_stored_in_clear_text_but_comes_back_readable()
    {
        var store = new ConfigStore(ConfigPath);
        AppConfig config = AppConfig.CreateDefault();
        config.Outbounds.Add(new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "proxy",
            Kind = OutboundKind.Socks5,
            Url = "socks5://127.0.0.1:1080",
            Username = "user",
            Password = "s3cret-passw0rd",
        });

        store.Save(config);

        string json = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("s3cret-passw0rd", json, StringComparison.Ordinal);

        AppConfig loaded = new ConfigStore(ConfigPath).Load();
        Outbound outbound = loaded.Outbounds.Find(o => o.Name == "proxy")!;
        Assert.Equal("s3cret-passw0rd", outbound.Password);
    }

    [Fact]
    public void Saving_does_not_encrypt_the_live_objects()
    {
        var store = new ConfigStore(ConfigPath);
        AppConfig config = AppConfig.CreateDefault();
        var outbound = new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "proxy",
            Kind = OutboundKind.Socks5,
            Url = "socks5://127.0.0.1:1080",
            Username = "user",
            Password = "plain",
        };
        config.Outbounds.Add(outbound);

        store.Save(config);

        // The engine keeps using these objects to build proxy sources — encrypting them in place
        // would break authentication until the next restart.
        Assert.Equal("plain", outbound.Password);
    }

    [Fact]
    public void Corrupted_file_is_set_aside_and_a_default_is_returned()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");

        AppConfig config = new ConfigStore(ConfigPath).Load();

        Assert.NotEmpty(config.Policies);
        Assert.True(File.Exists(ConfigPath + ".bak"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
