using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ProxyDivert.Api.Models;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Api.Extensions;

public static class LocalApiEndpointExtensions
{
    /// <summary>Every route of the local API, under /api. Errors leave as <see cref="ApiError"/> bodies.</summary>
    public static IEndpointRouteBuilder MapLocalApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api");

        // ==== status, engine, tunnels ====

        RouteGroupBuilder state = api.MapGroup("").WithTags("Status");
        state.MapGet("/status", (LocalApiService s) => s.GetStatusAsync())
            .WithSummary("Whether redirection is on, unsaved edits in the window, and each tunnel's state.");
        state.MapPut("/engine", async (SetRunningRequest body, LocalApiService s)
                => new SetRunningRequest { Running = await s.SetEngineRunningAsync(body.Running) })
            .WithSummary("Switches redirection on or off.");
        state.MapPost("/vpn/{outboundId:guid}/connect", (Guid outboundId, LocalApiService s)
                => s.SetVpnConnectedAsync(outboundId, true))
            .WithSummary("Brings a VPN outbound's tunnel up.");
        state.MapPost("/vpn/{outboundId:guid}/disconnect", (Guid outboundId, LocalApiService s)
                => s.SetVpnConnectedAsync(outboundId, false))
            .WithSummary("Takes a VPN outbound's tunnel down. 409 while a live filter routes through it.");

        // ==== outbounds (read-only for now) ====

        api.MapGet("/outbounds", (LocalApiService s) => s.GetOutboundsAsync())
            .WithTags("Outbounds")
            .WithSummary("Every outbound a policy can point at. Credentials and URLs are left out.");

        // ==== policies and their host rules ====

        RouteGroupBuilder policies = api.MapGroup("/policies").WithTags("Policies");
        policies.MapGet("", (LocalApiService s) => s.GetPoliciesAsync());
        policies.MapGet("/{id:guid}", (Guid id, LocalApiService s) => s.GetPolicyAsync(id));
        policies.MapPost("", (RoutingPolicy body, LocalApiService s) => s.CreatePolicyAsync(body))
            .WithSummary("Creates a policy. An empty or missing id is assigned.");
        policies.MapPut("/{id:guid}", (Guid id, RoutingPolicy body, LocalApiService s) => s.UpdatePolicyAsync(id, body))
            .WithSummary("Replaces a policy whole, its rules included.");
        policies.MapDelete("/{id:guid}", (Guid id, LocalApiService s) => s.DeletePolicyAsync(id))
            .WithSummary("Deletes a policy and every filter's reference to it. Default and the last policy stay.");
        policies.MapPut("/{id:guid}/outbound", (Guid id, SetOutboundRequest body, LocalApiService s)
                => s.SetPolicyOutboundAsync(id, body.OutboundId))
            .WithSummary("Points a policy at another outbound.");

        policies.MapGet("/{id:guid}/rules", (Guid id, LocalApiService s) => s.GetRulesAsync(id));
        policies.MapPost("/{id:guid}/rules", (Guid id, RoutingRule body, LocalApiService s) => s.AddRuleAsync(id, body))
            .WithSummary("Appends a host rule. An empty or missing id is assigned.");
        policies.MapPut("/{id:guid}/rules", (Guid id, List<RoutingRule> body, LocalApiService s) => s.ReplaceRulesAsync(id, body))
            .WithSummary("Replaces every host rule of the policy, tried in the order sent.");
        policies.MapPut("/{id:guid}/rules/{ruleId:guid}", (Guid id, Guid ruleId, RoutingRule body, LocalApiService s)
            => s.UpdateRuleAsync(id, ruleId, body));
        policies.MapDelete("/{id:guid}/rules/{ruleId:guid}", (Guid id, Guid ruleId, LocalApiService s)
            => s.DeleteRuleAsync(id, ruleId));

        // ==== process filters ====

        RouteGroupBuilder processes = api.MapGroup("/process-rules").WithTags("Process filters");
        processes.MapGet("", (LocalApiService s) => s.GetProcessRulesAsync());
        processes.MapGet("/{id:guid}", (Guid id, LocalApiService s) => s.GetProcessRuleAsync(id));
        processes.MapPost("", (ProcessRule body, LocalApiService s) => s.CreateProcessRuleAsync(body))
            .WithSummary("Creates a process filter. An empty or missing id is assigned.");
        processes.MapPut("/{id:guid}", (Guid id, ProcessRule body, LocalApiService s) => s.UpdateProcessRuleAsync(id, body));
        processes.MapDelete("/{id:guid}", (Guid id, LocalApiService s) => s.DeleteProcessRuleAsync(id));

        return app;
    }
}
