using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Services;
using ProxyDivert.ViewModels;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The three editing tabs each keep their own copy of the configuration's lists, and switching tabs
// is what refreshes them. A policy made on the Rules tab has to be in the Processes tab's list the
// moment the user steps across, and the refresh must not cost the user the row they had picked.
public class TabReloadTests
{
    [Fact]
    public async Task Processes_tab_sees_a_policy_made_elsewhere_and_keeps_its_selection()
    {
        await WithServices(services =>
        {
            ProcessRule kept = Filter("one.exe");
            services.Config.ProcessRules.Add(kept);
            var model = new ProcessesViewModel(services);
            model.SelectedRule = model.Rules.Single(r => r.Id == kept.Id);

            var policy = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Made on Rules" };
            services.Config.Policies.Add(policy);
            services.Config.ProcessRules.Add(Filter("two.exe"));
            model.Reload();

            Assert.Contains(model.Policies, p => p.Id == policy.Id);
            Assert.Equal(2, model.Rules.Count);
            Assert.Equal(kept.Id, model.SelectedRule?.Id);
        });
    }

    [Fact]
    public async Task Processes_tab_clears_the_selection_when_that_filter_is_gone()
    {
        await WithServices(services =>
        {
            ProcessRule doomed = Filter("one.exe");
            services.Config.ProcessRules.Add(doomed);
            var model = new ProcessesViewModel(services);
            model.SelectedRule = model.Rules.Single();

            services.Config.ProcessRules.Remove(doomed);
            model.Reload();

            Assert.Empty(model.Rules);
            Assert.Null(model.SelectedRule);
        });
    }

    [Fact]
    public async Task Rules_tab_keeps_the_selected_policy_and_sees_a_new_outbound()
    {
        await WithServices(services =>
        {
            var work = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Work" };
            services.Config.Policies.Add(work);
            var model = new RulesViewModel(services);
            model.SelectedPolicy = model.Policies.Single(p => p.Id == work.Id);

            Outbound outbound = Proxy("made on Outbounds");
            services.Config.Outbounds.Add(outbound);
            model.Reload();

            Assert.Contains(model.Outbounds, o => o.Id == outbound.Id);
            Assert.Equal(work.Id, model.SelectedPolicy?.Id);
        });
    }

    // Unlike the other two, the Rules tab always has a policy up: an empty rule grid with no policy
    // over it reads as "this policy has no rules", which is not what happened.
    [Fact]
    public async Task Rules_tab_falls_back_to_the_first_policy_when_the_selected_one_is_gone()
    {
        await WithServices(services =>
        {
            var work = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Work" };
            services.Config.Policies.Add(work);
            var model = new RulesViewModel(services);
            model.SelectedPolicy = model.Policies.Single(p => p.Id == work.Id);

            services.Config.Policies.Remove(work);
            model.Reload();

            Assert.Equal(services.Config.Policies[0].Id, model.SelectedPolicy?.Id);
        });
    }

    [Fact]
    public async Task Outbounds_tab_keeps_its_selection_and_drops_it_when_the_outbound_is_gone()
    {
        await WithServices(services =>
        {
            Outbound outbound = Proxy("proxy 1");
            services.Config.Outbounds.Add(outbound);
            using var model = new OutboundsViewModel(services);
            model.Selected = model.Outbounds.Single(o => o.Id == outbound.Id);

            model.Reload();
            Assert.Equal(outbound.Id, model.Selected?.Id);

            services.Config.Outbounds.Remove(outbound);
            model.Reload();
            Assert.Null(model.Selected);
        });
    }

    private static async Task WithServices(Action<AppServices> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"proxydivert-reload-{Guid.NewGuid():N}.json");
        try
        {
            await using var services = new AppServices(path);
            test(services);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static Outbound Proxy(string name) => new Outbound
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = OutboundKind.HttpProxy,
    };

    private static ProcessRule Filter(string pattern) => new ProcessRule
    {
        Id = Guid.NewGuid(),
        Name = pattern,
        Condition = ConditionGroup.CreateDefault(pattern),
    };
}
