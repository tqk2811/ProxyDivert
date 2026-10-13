using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ProxyDivert.Api.Models;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Api;

/// <summary>
/// The local API as MCP tools, for an LLM client. Every tool is a thin call into
/// <see cref="LocalApiService"/>, so validation and the 409-while-unsaved rule are the HTTP API's.
/// </summary>
/// <remarks>
/// A refused request (<see cref="ApiException"/>) leaves as a tool error carrying the API's code
/// and message, so the model can read why and correct itself instead of the call failing blind.
/// </remarks>
[McpServerToolType]
public sealed class LocalMcpTools
{
    private const string WriteNote =
        " Changes are saved and applied at once, like the window's Save button. Refused with CONFLICT"
        + " while the window holds unsaved edits.";

    private readonly LocalApiService _service;
    private readonly ILogger<LocalMcpTools> _logger;

    public LocalMcpTools(LocalApiService service, ILogger<LocalMcpTools> logger)
    {
        _service = service;
        _logger = logger;
    }

    // ==== status, engine, tunnels ====

    [McpServerTool(Name = "get_status", ReadOnly = true, Idempotent = true)]
    [Description("Whether ProxyDivert is redirecting traffic (engine running), whether the window holds unsaved"
        + " edits (every write is refused until they are saved), and each VPN outbound's tunnel state.")]
    public Task<ApiStatus> GetStatus() => Run(() => _service.GetStatusAsync());

    [McpServerTool(Name = "set_engine_running", Destructive = true, Idempotent = true)]
    [Description("Switches traffic redirection on (true) or off (false). Returns whether it is running afterwards.")]
    public Task<bool> SetEngineRunning(
        [Description("True to start redirecting, false to stop.")] bool running)
        => Run(() => _service.SetEngineRunningAsync(running));

    [McpServerTool(Name = "connect_vpn", Destructive = true, Idempotent = true)]
    [Description("Brings up the tunnel of a VPN outbound. Get outbound ids from list_outbounds.")]
    public Task<string> ConnectVpn(
        [Description("Id of a VPN outbound.")] Guid outboundId)
        => Run(async () =>
        {
            await _service.SetVpnConnectedAsync(outboundId, true).ConfigureAwait(false);
            return "connect requested";
        });

    [McpServerTool(Name = "disconnect_vpn", Destructive = true, Idempotent = true)]
    [Description("Takes down the tunnel of a VPN outbound. Refused with CONFLICT while a live filter routes through it.")]
    public Task<string> DisconnectVpn(
        [Description("Id of a VPN outbound.")] Guid outboundId)
        => Run(async () =>
        {
            await _service.SetVpnConnectedAsync(outboundId, false).ConfigureAwait(false);
            return "disconnect requested";
        });

    // ==== outbounds ====

    [McpServerTool(Name = "list_outbounds", ReadOnly = true, Idempotent = true)]
    [Description("Every outbound (Direct, Block, proxies, VPNs, SSH...) a policy can send traffic through, with"
        + " id, name, kind and whether it is enabled. Credentials and URLs are left out.")]
    public Task<List<ApiOutbound>> ListOutbounds() => Run(() => _service.GetOutboundsAsync());

    // ==== policies ====

    [McpServerTool(Name = "list_policies", ReadOnly = true, Idempotent = true)]
    [Description("Every routing policy: a named outbound choice plus host rules. Process filters point at"
        + " policies; inside a policy the host rules decide per destination.")]
    public Task<List<RoutingPolicy>> ListPolicies() => Run(() => _service.GetPoliciesAsync());

    [McpServerTool(Name = "get_policy", ReadOnly = true, Idempotent = true)]
    [Description("One routing policy with its host rules.")]
    public Task<RoutingPolicy> GetPolicy(
        [Description("Policy id.")] Guid id)
        => Run(() => _service.GetPolicyAsync(id));

    [McpServerTool(Name = "create_policy")]
    [Description("Creates a routing policy and returns it. Leave id out (or empty) to have one assigned." + WriteNote)]
    public Task<RoutingPolicy> CreatePolicy(
        [Description("The policy. Tip: read an existing one with get_policy to see the shape.")] RoutingPolicy policy)
        => Run(() => _service.CreatePolicyAsync(policy));

    [McpServerTool(Name = "update_policy", Idempotent = true)]
    [Description("Replaces a policy whole, its host rules included. Read it first with get_policy and send it"
        + " back modified." + WriteNote)]
    public Task<RoutingPolicy> UpdatePolicy(
        [Description("Id of the policy to replace.")] Guid id,
        [Description("The full new policy.")] RoutingPolicy policy)
        => Run(() => _service.UpdatePolicyAsync(id, policy));

    [McpServerTool(Name = "delete_policy", Destructive = true, Idempotent = true)]
    [Description("Deletes a policy and every process filter's reference to it. The built-in Default and the"
        + " last policy cannot be deleted." + WriteNote)]
    public Task<string> DeletePolicy(
        [Description("Policy id.")] Guid id)
        => Run(async () =>
        {
            await _service.DeletePolicyAsync(id).ConfigureAwait(false);
            return "deleted";
        });

    [McpServerTool(Name = "set_policy_outbound", Idempotent = true)]
    [Description("Points a policy at another outbound (ids from list_outbounds). Returns the policy." + WriteNote)]
    public Task<RoutingPolicy> SetPolicyOutbound(
        [Description("Policy id.")] Guid id,
        [Description("Outbound id.")] Guid outboundId)
        => Run(() => _service.SetPolicyOutboundAsync(id, outboundId));

    // ==== host rules of a policy ====

    [McpServerTool(Name = "list_policy_rules", ReadOnly = true, Idempotent = true)]
    [Description("The host rules of a policy, in the order they are tried.")]
    public Task<List<RoutingRule>> ListPolicyRules(
        [Description("Policy id.")] Guid policyId)
        => Run(() => _service.GetRulesAsync(policyId));

    [McpServerTool(Name = "add_policy_rule")]
    [Description("Appends a host rule to a policy and returns it. Leave id out to have one assigned." + WriteNote)]
    public Task<RoutingRule> AddPolicyRule(
        [Description("Policy id.")] Guid policyId,
        [Description("The rule.")] RoutingRule rule)
        => Run(() => _service.AddRuleAsync(policyId, rule));

    [McpServerTool(Name = "replace_policy_rules", Idempotent = true)]
    [Description("Replaces every host rule of a policy; they are tried in the order sent." + WriteNote)]
    public Task<List<RoutingRule>> ReplacePolicyRules(
        [Description("Policy id.")] Guid policyId,
        [Description("The complete new rule list.")] List<RoutingRule> rules)
        => Run(() => _service.ReplaceRulesAsync(policyId, rules));

    [McpServerTool(Name = "update_policy_rule", Idempotent = true)]
    [Description("Replaces one host rule of a policy." + WriteNote)]
    public Task<RoutingRule> UpdatePolicyRule(
        [Description("Policy id.")] Guid policyId,
        [Description("Id of the rule to replace.")] Guid ruleId,
        [Description("The full new rule.")] RoutingRule rule)
        => Run(() => _service.UpdateRuleAsync(policyId, ruleId, rule));

    [McpServerTool(Name = "delete_policy_rule", Destructive = true, Idempotent = true)]
    [Description("Deletes one host rule of a policy." + WriteNote)]
    public Task<string> DeletePolicyRule(
        [Description("Policy id.")] Guid policyId,
        [Description("Rule id.")] Guid ruleId)
        => Run(async () =>
        {
            await _service.DeleteRuleAsync(policyId, ruleId).ConfigureAwait(false);
            return "deleted";
        });

    // ==== process filters ====

    [McpServerTool(Name = "list_process_rules", ReadOnly = true, Idempotent = true)]
    [Description("Every process filter: which programs (by name, command line, AND/OR condition tree) get"
        + " their traffic redirected, and through which policies.")]
    public Task<List<ProcessRule>> ListProcessRules() => Run(() => _service.GetProcessRulesAsync());

    [McpServerTool(Name = "get_process_rule", ReadOnly = true, Idempotent = true)]
    [Description("One process filter.")]
    public Task<ProcessRule> GetProcessRule(
        [Description("Process filter id.")] Guid id)
        => Run(() => _service.GetProcessRuleAsync(id));

    [McpServerTool(Name = "create_process_rule")]
    [Description("Creates a process filter and returns it. Leave id out to have one assigned. Tip: read an"
        + " existing one with list_process_rules to see the shape. To route one running instance (e.g. one"
        + " of many windows of the same exe) use a process condition with matcher \"Pid\" and the decimal"
        + " process id as pattern; it compares the id only, and since ids are reused, delete the filter"
        + " when that process exits." + WriteNote)]
    public Task<ProcessRule> CreateProcessRule(
        [Description("The process filter.")] ProcessRule rule)
        => Run(() => _service.CreateProcessRuleAsync(rule));

    [McpServerTool(Name = "update_process_rule", Idempotent = true)]
    [Description("Replaces a process filter whole." + WriteNote)]
    public Task<ProcessRule> UpdateProcessRule(
        [Description("Id of the process filter to replace.")] Guid id,
        [Description("The full new process filter.")] ProcessRule rule)
        => Run(() => _service.UpdateProcessRuleAsync(id, rule));

    [McpServerTool(Name = "delete_process_rule", Destructive = true, Idempotent = true)]
    [Description("Deletes a process filter." + WriteNote)]
    public Task<string> DeleteProcessRule(
        [Description("Process filter id.")] Guid id)
        => Run(async () =>
        {
            await _service.DeleteProcessRuleAsync(id).ConfigureAwait(false);
            return "deleted";
        });

    // The SDK turns an McpException into an error result with its message; any other exception
    // would reach the model as a bare "an error occurred". A malformed argument is the model's to
    // fix, so its message is kept; anything else is a fault here, logged and reported generically.
    private async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (McpException)
        {
            throw;
        }
        catch (ApiException ex)
        {
            string message = $"{ex.Code} ({ex.StatusCode}): {ex.Message}";
            if (ex.Details is not null) message += " Details: " + JsonSerializer.Serialize(ex.Details);
            throw new McpException(message, ex);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        {
            throw new McpException($"INVALID_REQUEST: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "MCP tool failed");
            throw new McpException("INTERNAL_ERROR: the tool failed unexpectedly; see the ProxyDivert log.", ex);
        }
    }
}
