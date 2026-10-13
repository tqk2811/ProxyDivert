using ProxyDivert.Api.Interfaces;
using ProxyDivert.Api.Models;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing.Compiled;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Api;

/// <summary>
/// What each endpoint does to the configuration. Every write goes through
/// <see cref="IApiBackend.EditAsync"/>, which hands it a copy: a request that throws half-way has
/// changed nothing, and one that returns is applied as a whole, like a press of Save.
/// </summary>
public sealed class LocalApiService
{
    private readonly IApiBackend _backend;

    public LocalApiService(IApiBackend backend)
    {
        _backend = backend;
    }

    public Task<ApiStatus> GetStatusAsync() => _backend.GetStatusAsync();

    public Task<bool> SetEngineRunningAsync(bool running) => _backend.SetEngineRunningAsync(running);

    public async Task SetVpnConnectedAsync(Guid outboundId, bool connected)
    {
        await _backend.ReadAsync(config => FindOutbound(config, outboundId)).ConfigureAwait(false);
        await _backend.SetVpnConnectedAsync(outboundId, connected).ConfigureAwait(false);
    }

    // ==== outbounds ====

    public Task<List<ApiOutbound>> GetOutboundsAsync()
        => _backend.ReadAsync(config => config.Outbounds.Select(ApiOutbound.From).ToList());

    // ==== policies ====

    public Task<List<RoutingPolicy>> GetPoliciesAsync()
        => _backend.ReadAsync(config => config.Policies.Select(SortedRules).ToList());

    public Task<RoutingPolicy> GetPolicyAsync(Guid id)
        => _backend.ReadAsync(config => SortedRules(FindPolicy(config, id)));

    public Task<RoutingPolicy> CreatePolicyAsync(RoutingPolicy policy)
        => _backend.EditAsync(config =>
        {
            if (policy.Id == Guid.Empty) policy.Id = Guid.NewGuid();
            if (config.Policies.Any(p => p.Id == policy.Id))
                throw ApiException.Conflict("POLICY_EXISTS", $"A policy with id {policy.Id} already exists.");
            PreparePolicy(config, policy);
            config.Policies.Add(policy);
            return policy;
        });

    public Task<RoutingPolicy> UpdatePolicyAsync(Guid id, RoutingPolicy policy)
        => _backend.EditAsync(config =>
        {
            int index = config.Policies.FindIndex(p => p.Id == id);
            if (index < 0) throw PolicyNotFound(id);
            policy.Id = id;
            if (policy.IsBuiltIn)
            {
                // Its name and its catch-all rule are fixed; everything else may change.
                policy.Name = config.Policies[index].Name;
                policy.Rules = config.Policies[index].Rules;
            }
            PreparePolicy(config, policy);
            config.Policies[index] = policy;
            return policy;
        });

    public Task DeletePolicyAsync(Guid id)
        => _backend.EditAsync(config =>
        {
            FindPolicy(config, id);
            if (!config.RemovePolicy(id))
                throw ApiException.BadRequest(
                    "POLICY_NOT_REMOVABLE", "The built-in Default policy and the last policy cannot be deleted.");
            return true;
        });

    public Task<RoutingPolicy> SetPolicyOutboundAsync(Guid id, Guid outboundId)
        => _backend.EditAsync(config =>
        {
            RoutingPolicy policy = FindPolicy(config, id);
            EnsureOutboundExists(config, outboundId);
            policy.OutboundId = outboundId;
            return policy;
        });

    // ==== a policy's host rules ====

    public Task<List<RoutingRule>> GetRulesAsync(Guid policyId)
        => _backend.ReadAsync(config => FindPolicy(config, policyId).Rules.OrderBy(r => r.Order).ToList());

    public Task<RoutingRule> AddRuleAsync(Guid policyId, RoutingRule rule)
        => _backend.EditAsync(config =>
        {
            RoutingPolicy policy = FindEditableRules(config, policyId);
            if (rule.Id == Guid.Empty) rule.Id = Guid.NewGuid();
            if (policy.Rules.Any(r => r.Id == rule.Id))
                throw ApiException.Conflict("RULE_EXISTS", $"A rule with id {rule.Id} already exists in this policy.");
            rule.Order = policy.Rules.Count == 0 ? 0 : policy.Rules.Max(r => r.Order) + 1;
            policy.Rules.Add(rule);
            EnsurePatternsCompile(policy);
            return rule;
        });

    public Task<RoutingRule> UpdateRuleAsync(Guid policyId, Guid ruleId, RoutingRule rule)
        => _backend.EditAsync(config =>
        {
            RoutingPolicy policy = FindEditableRules(config, policyId);
            int index = policy.Rules.FindIndex(r => r.Id == ruleId);
            if (index < 0) throw ApiException.NotFound("RULE_NOT_FOUND", $"No rule {ruleId} in policy {policyId}.");
            rule.Id = ruleId;
            // Where the rule sits is the list's business, not this request's.
            rule.Order = policy.Rules[index].Order;
            policy.Rules[index] = rule;
            EnsurePatternsCompile(policy);
            return rule;
        });

    public Task DeleteRuleAsync(Guid policyId, Guid ruleId)
        => _backend.EditAsync(config =>
        {
            RoutingPolicy policy = FindEditableRules(config, policyId);
            if (policy.Rules.RemoveAll(r => r.Id == ruleId) == 0)
                throw ApiException.NotFound("RULE_NOT_FOUND", $"No rule {ruleId} in policy {policyId}.");
            return true;
        });

    /// <summary>Replaces every rule of the policy, in the order given — for a tool that syncs a whole list.</summary>
    public Task<List<RoutingRule>> ReplaceRulesAsync(Guid policyId, List<RoutingRule> rules)
        => _backend.EditAsync(config =>
        {
            RoutingPolicy policy = FindEditableRules(config, policyId);
            PrepareRules(rules);
            policy.Rules = rules;
            EnsurePatternsCompile(policy);
            return rules;
        });

    // ==== process filters ====

    public Task<List<ProcessRule>> GetProcessRulesAsync()
        => _backend.ReadAsync(config => config.ProcessRules.ToList());

    public Task<ProcessRule> GetProcessRuleAsync(Guid id)
        => _backend.ReadAsync(config => FindProcessRule(config, id));

    public Task<ProcessRule> CreateProcessRuleAsync(ProcessRule rule)
        => _backend.EditAsync(config =>
        {
            if (rule.Id == Guid.Empty) rule.Id = Guid.NewGuid();
            if (config.ProcessRules.Any(r => r.Id == rule.Id))
                throw ApiException.Conflict("PROCESS_RULE_EXISTS", $"A process filter with id {rule.Id} already exists.");
            PrepareProcessRule(config, rule);
            config.ProcessRules.Add(rule);
            return rule;
        });

    public Task<ProcessRule> UpdateProcessRuleAsync(Guid id, ProcessRule rule)
        => _backend.EditAsync(config =>
        {
            int index = config.ProcessRules.FindIndex(r => r.Id == id);
            if (index < 0) throw ProcessRuleNotFound(id);
            rule.Id = id;
            PrepareProcessRule(config, rule);
            config.ProcessRules[index] = rule;
            return rule;
        });

    public Task DeleteProcessRuleAsync(Guid id)
        => _backend.EditAsync(config =>
        {
            if (config.ProcessRules.RemoveAll(r => r.Id == id) == 0) throw ProcessRuleNotFound(id);
            return true;
        });

    // ==== checks ====

    private static void PreparePolicy(AppConfig config, RoutingPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Name))
            throw ApiException.BadRequest("NAME_REQUIRED", "A policy needs a name.");
        EnsureOutboundExists(config, policy.OutboundId);
        policy.Rules ??= new List<RoutingRule>();
        PrepareRules(policy.Rules);
        EnsurePatternsCompile(policy);
    }

    // Ids filled in, and the order the list was sent in kept as the order the rules are tried in.
    private static void PrepareRules(List<RoutingRule> rules)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            if (rules[i].Id == Guid.Empty) rules[i].Id = Guid.NewGuid();
            rules[i].Order = i;
        }
        if (rules.Select(r => r.Id).Distinct().Count() != rules.Count)
            throw ApiException.BadRequest("DUPLICATE_RULE_ID", "Two rules in the list share an id.");
    }

    // The same compile the engine does: a pattern it cannot parse matches nothing (or, with Not,
    // everything), so it is refused here instead of being saved to fail silently.
    private static void EnsurePatternsCompile(RoutingPolicy policy)
    {
        var errors = new List<RulePatternError>();
        CompiledRuleSet.CompilePolicy(policy, errors);
        if (errors.Count == 0) return;
        throw ApiException.BadRequest(
            "INVALID_PATTERN", "One or more rule patterns cannot be parsed.",
            errors.Select(e => new { e.RuleId, e.Matcher, e.Pattern, e.Message }).ToList());
    }

    private static void PrepareProcessRule(AppConfig config, ProcessRule rule)
    {
        rule.PolicyIds ??= new List<Guid>();
        rule.PolicyOrder ??= new List<Guid>();
        Guid? missing = rule.PolicyIds.Concat(rule.PolicyOrder)
            .Cast<Guid?>()
            .FirstOrDefault(id => config.Policies.All(p => p.Id != id));
        if (missing is not null)
            throw ApiException.BadRequest("POLICY_NOT_FOUND", $"No policy {missing}.");
        EnsurePidPatternsParse(rule.Condition);
    }

    // A Pid leaf whose text is not a process id never matches anything, so it is refused here like
    // a regex that will not compile instead of being saved to fail silently. An empty one is left
    // alone: the editor treats an empty leaf as "not filled in yet" for every matcher.
    private static void EnsurePidPatternsParse(ProcessCondition? condition)
    {
        switch (condition)
        {
            case ProcessNameCondition { Matcher: ProcessMatcherType.Pid } leaf
                when !string.IsNullOrWhiteSpace(leaf.Pattern) && !ProcessNameCondition.TryParsePid(leaf.Pattern, out _):
                throw ApiException.BadRequest(
                    "INVALID_PATTERN", $"'{leaf.Pattern}' is not a process id; a Pid pattern is a decimal number.");
            case ConditionGroup group:
                foreach (ProcessCondition child in group.Children) EnsurePidPatternsParse(child);
                break;
        }
    }

    private static void EnsureOutboundExists(AppConfig config, Guid outboundId)
    {
        if (config.Outbounds.All(o => o.Id != outboundId))
            throw ApiException.BadRequest("OUTBOUND_NOT_FOUND", $"No outbound {outboundId}.");
    }

    private static Outbound FindOutbound(AppConfig config, Guid id)
        => config.Outbounds.FirstOrDefault(o => o.Id == id)
            ?? throw ApiException.NotFound("OUTBOUND_NOT_FOUND", $"No outbound {id}.");

    private static RoutingPolicy FindPolicy(AppConfig config, Guid id)
        => config.Policies.FirstOrDefault(p => p.Id == id) ?? throw PolicyNotFound(id);

    // The built-in Default always has its one catch-all rule; the configuration puts it back on every
    // save, so an edit would be answered 200 and then silently undone.
    private static RoutingPolicy FindEditableRules(AppConfig config, Guid id)
    {
        RoutingPolicy policy = FindPolicy(config, id);
        if (policy.IsBuiltIn)
            throw ApiException.BadRequest("POLICY_BUILT_IN", "The built-in Default policy's rules cannot be changed.");
        return policy;
    }

    private static ProcessRule FindProcessRule(AppConfig config, Guid id)
        => config.ProcessRules.FirstOrDefault(r => r.Id == id) ?? throw ProcessRuleNotFound(id);

    private static ApiException PolicyNotFound(Guid id)
        => ApiException.NotFound("POLICY_NOT_FOUND", $"No policy {id}.");

    private static ApiException ProcessRuleNotFound(Guid id)
        => ApiException.NotFound("PROCESS_RULE_NOT_FOUND", $"No process filter {id}.");

    // Read results are copies made by the backend, so sorting in place touches nothing live.
    private static RoutingPolicy SortedRules(RoutingPolicy policy)
    {
        policy.Rules = policy.Rules.OrderBy(r => r.Order).ToList();
        return policy;
    }
}
