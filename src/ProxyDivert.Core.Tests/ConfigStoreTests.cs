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
            OutboundId = Outbound.BlockId,
            Order = 5,
        });
        config.ProcessRules.Add(new ProcessRule
        {
            Id = Guid.NewGuid(),
            Name = "Chrome",
            PolicyId = policyId,
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

    // The upgrade that has to be exact. A filter written by the two-slot version decides which
    // processes leave the machine through the proxy; one that comes back meaning something else is
    // a program quietly running direct, and the user finds out by leaking their address.
    [Fact]
    public void A_v2_process_rule_becomes_the_same_filter_as_a_condition_tree()
    {
        File.WriteAllText(ConfigPath, """
            {
              "Version": 2,
              "Outbounds": [],
              "Policies": [],
              "ProcessRules": [
                {
                  "Id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
                  "Matcher": "ExeName",
                  "Pattern": "java.exe",
                  "ArgumentMatcher": "Contains",
                  "ArgumentPattern": "minecraft",
                  "IncludeChildren": true,
                  "PolicyId": "6f9619ff-8b86-d011-b42d-00cf4fc964aa",
                  "IsEnabled": true
                }
              ]
            }
            """);

        AppConfig config = new ConfigStore(ConfigPath).Load();
        ProcessRule filter = Assert.Single(config.ProcessRules);

        // The two slots were ANDed, so they become one "match all" group of two conditions.
        var group = Assert.IsType<ConditionGroup>(filter.Condition);
        Assert.Equal(ConditionOperator.All, group.Operator);
        Assert.Equal(ProcessMatcherType.ExeName, Assert.IsType<ProcessNameCondition>(group.Children[0]).Matcher);
        Assert.Equal("java.exe", Assert.IsType<ProcessNameCondition>(group.Children[0]).Pattern);
        Assert.Equal("minecraft", Assert.IsType<CommandLineCondition>(group.Children[1]).Pattern);

        // Filters had no name, so the row keeps saying what the user recognised it by.
        Assert.Equal("java.exe", filter.Name);

        // And it still decides exactly what it decided before.
        Assert.True(ProcessRuleMatcher.IsMatch(filter, "java", null, "java.exe -Dminecraft"));
        Assert.False(ProcessRuleMatcher.IsMatch(filter, "java", null, "java.exe -Declipse"));
        Assert.False(ProcessRuleMatcher.IsMatch(filter, "python", null, "python.exe -Dminecraft"));
    }

    // An empty argument slot was never consulted, so it must not come back as a row in the tree —
    // a filter nobody touched should not open looking half-edited.
    [Fact]
    public void A_v2_rule_with_no_argument_slot_upgrades_to_a_single_condition()
    {
        File.WriteAllText(ConfigPath, """
            {
              "Version": 2,
              "Outbounds": [],
              "Policies": [],
              "ProcessRules": [
                {
                  "Id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
                  "Matcher": "FullPath",
                  "Pattern": "C:\\Games\\client.exe",
                  "PolicyId": "6f9619ff-8b86-d011-b42d-00cf4fc964aa",
                  "IsEnabled": true
                }
              ]
            }
            """);

        AppConfig config = new ConfigStore(ConfigPath).Load();
        var group = Assert.IsType<ConditionGroup>(Assert.Single(config.ProcessRules).Condition);

        Assert.Single(group.Children);
        Assert.Equal(ProcessMatcherType.FullPath, Assert.IsType<ProcessNameCondition>(group.Children[0]).Matcher);
    }

    // The old slots must not survive alongside the tree: two copies of one condition, only one of
    // which anything reads, is the shape a later "why is this filter ignoring me" comes from.
    [Fact]
    public void The_old_two_slot_fields_are_gone_from_the_file_after_a_save()
    {
        File.WriteAllText(ConfigPath, """
            {
              "Version": 2,
              "Outbounds": [],
              "Policies": [],
              "ProcessRules": [
                {
                  "Id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
                  "Matcher": "ExeName",
                  "Pattern": "java.exe",
                  "ArgumentPattern": "minecraft",
                  "PolicyId": "6f9619ff-8b86-d011-b42d-00cf4fc964aa"
                }
              ]
            }
            """);

        var store = new ConfigStore(ConfigPath);
        store.Save(store.Load());

        string json = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("\"ArgumentPattern\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"commandLine\"", json, StringComparison.Ordinal);
        Assert.Equal(AppConfig.CurrentVersion, new ConfigStore(ConfigPath).Load().Version);
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

    [Fact]
    public void A_v1_file_that_blocked_ipv6_keeps_blocking_it()
    {
        File.WriteAllText(ConfigPath, """
            { "Version": 1, "BlockIpv6": true, "Outbounds": [], "Policies": [], "ProcessRules": [] }
            """);

        AppConfig config = new ConfigStore(ConfigPath).Load();

        Assert.Equal(Ipv6Mode.Block, config.Ipv6);
        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Null(config.BlockIpv6);
    }

    [Fact]
    public void A_v1_file_that_let_ipv6_through_now_redirects_it()
    {
        // "Don't block" used to mean the target's IPv6 escaped the proxy — the only thing the code
        // could do then. Redirecting it is what that setting was actually asking for.
        File.WriteAllText(ConfigPath, """
            { "Version": 1, "BlockIpv6": false, "Outbounds": [], "Policies": [], "ProcessRules": [] }
            """);

        AppConfig config = new ConfigStore(ConfigPath).Load();

        Assert.Equal(Ipv6Mode.Redirect, config.Ipv6);
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
