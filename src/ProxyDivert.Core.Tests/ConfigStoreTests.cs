using System;
using System.IO;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using System.Linq;
using TqkLibrary.WinDivert.Redirect.Enums;
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
            Order = 5,
        });
        config.ProcessRules.Add(new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = "Chrome",
            PolicyIds = { policyId },
            Condition = new ConditionGroup
            {
                Operator = ConditionOperator.All,
                Children =
                {
                    new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = "chrome.exe" },
                    new CommandLineCondition
                    {
                        Matcher = ArgumentMatcherType.Contains,
                        Pattern = "--profile-directory",
                        Negate = true,
                    },
                },
            },
        });

        store.Save(config);
        AppConfig loaded = new ConfigStore(ConfigPath).Load();

        RoutingRule rule = Assert.Single(loaded.Policies[0].Rules);
        Assert.Equal(HostMatcherType.Wildcard, rule.Matcher);
        Assert.Equal("*.google.com", rule.Pattern);
        Assert.Equal(5, rule.Order);

        // The condition tree is written through a type discriminator, so this is also the check
        // that a group comes back as a group and a leaf as the right kind of leaf.
        ProcessRule filter = Assert.Single(loaded.ProcessRules);
        Assert.Equal("Chrome", filter.Name);
        var group = Assert.IsType<ConditionGroup>(filter.Condition);
        Assert.Equal(ConditionOperator.All, group.Operator);

        var name = Assert.IsType<ProcessNameCondition>(group.Children[0]);
        Assert.Equal("chrome.exe", name.Pattern);

        var arguments = Assert.IsType<CommandLineCondition>(group.Children[1]);
        Assert.Equal("--profile-directory", arguments.Pattern);
        Assert.True(arguments.Negate);
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

    // For a while the outbound grid let a click land on the type picker of a row that was never
    // meant to take one — the grid was read-only, which stops text cells but not combo columns —
    // so Direct could be saved as an HTTP proxy with no URL. Everything routed to Direct then goes
    // to a proxy that is not there. The interface no longer allows it; a file already written that
    // way has to come back usable.
    [Fact]
    public void A_built_in_outbound_that_was_edited_into_something_else_comes_back_as_itself()
    {
        File.WriteAllText(ConfigPath, $$"""
            {
              "Outbounds": [
                {
                  "Id": "{{Outbound.DirectId}}", "Name": "Direct", "Kind": "HttpProxy",
                  "Url": "http://127.0.0.1:8080", "Username": "u", "Password": "p",
                  "IsEnabled": false
                },
                { "Id": "{{Outbound.BlockId}}", "Name": "Block", "Kind": "Socks5" }
              ],
              "Policies": [], "ProcessRules": []
            }
            """);

        AppConfig config = new ConfigStore(ConfigPath).Load();

        Outbound direct = config.Outbounds.Single(o => o.Id == Outbound.DirectId);
        Assert.Equal(OutboundKind.Direct, direct.Kind);
        Assert.Null(direct.Url);
        Assert.Null(direct.Username);
        Assert.Null(direct.Password);
        Assert.True(direct.IsEnabled);

        Assert.Equal(OutboundKind.Block, config.Outbounds.Single(o => o.Id == Outbound.BlockId).Kind);

        // The name is the one thing that is the user's to change: rules point at these by id.
        Assert.Equal("Direct", direct.Name);
    }

    [Fact]
    public void The_ipv6_mode_survives_a_save_and_load()
    {
        var store = new ConfigStore(ConfigPath);
        AppConfig config = AppConfig.CreateDefault();
        config.Ipv6 = Ipv6Mode.Block;
        config.Outbounds.Add(new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "vpn-ish",
            Kind = OutboundKind.Socks5,
            Url = "socks5://127.0.0.1:1080",
            Ipv6Support = Ipv6Support.Disabled,
        });

        store.Save(config);
        AppConfig loaded = store.Load();

        Assert.Equal(Ipv6Mode.Block, loaded.Ipv6);
        Assert.Equal(Ipv6Support.Disabled, loaded.Outbounds.Single(o => o.Name == "vpn-ish").Ipv6Support);
        // A brand-new outbound has no answer for this yet, and must not pretend it does.
        Assert.Equal(Ipv6Support.Auto, loaded.Outbounds.Single(o => o.Kind == OutboundKind.Direct).Ipv6Support);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
