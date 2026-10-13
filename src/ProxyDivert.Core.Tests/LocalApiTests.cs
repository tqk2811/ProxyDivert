using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using ProxyDivert.Api;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The API over a real Kestrel on a loopback port of its own, against a fake backend: what goes over
// the wire, what reaches the configuration, and what is refused.
public sealed class LocalApiTests : IAsyncLifetime
{
    private readonly FakeApiBackend _backend = new FakeApiBackend();
    private readonly LocalApiSettings _settings = new LocalApiSettings { Enabled = true, Port = 0 };
    private readonly LocalApiHost _host;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions Json = CreateJson();

    // A policy of the user's own: the built-in Default's rules are fixed.
    private readonly Guid _policyId = Guid.NewGuid();

    public LocalApiTests()
    {
        _backend.Config.Policies.Add(new RoutingPolicy { Id = _policyId, Name = "Mine" });
        _host = new LocalApiHost(_backend, () => _settings);
    }

    public async Task InitializeAsync()
    {
        string? error = await _host.ApplyAsync(_settings);
        Assert.True(error is null, error);
        _client = new HttpClient { BaseAddress = _host.Address };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions();
        LocalApiHost.ConfigureJson(options);
        return options;
    }

    private async Task<string?> ErrorCode(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    // ==== listening and the token ====

    [Fact]
    public void Loopback_mode_listens_on_loopback_only()
    {
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(_host.Address!.Host)));
    }

    [Fact]
    public async Task Without_require_token_a_bare_request_is_served()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task With_require_token_a_missing_or_wrong_token_is_refused()
    {
        _settings.RequireToken = true;
        _settings.Token = "secret-token";

        HttpResponseMessage bare = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ErrorCode(bare));

        var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(wrong)).StatusCode);

        var right = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        right.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(right)).StatusCode);
    }

    [Fact]
    public async Task Require_token_with_no_token_set_refuses_everything()
    {
        _settings.RequireToken = true;
        _settings.Token = null;

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_swagger_document_is_served_without_a_token()
    {
        _settings.RequireToken = true;
        _settings.Token = "secret-token";

        HttpResponseMessage response = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/policies/{id}/rules", await response.Content.ReadAsStringAsync());
    }

    // ==== reads ====

    [Fact]
    public async Task Outbounds_carry_no_credentials()
    {
        _backend.Config.Outbounds.Add(new Outbound
        {
            Id = Guid.NewGuid(), Name = "proxy", Kind = OutboundKind.Socks5,
            Url = "socks5://user:pw@10.0.0.1:1080", Username = "user", Password = "hunter2",
        });

        string body = await _client.GetStringAsync("/api/outbounds");

        Assert.Contains("\"proxy\"", body);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("10.0.0.1", body);
    }

    // ==== host rules ====

    [Fact]
    public async Task Adding_a_rule_appends_it_and_saves_once()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/policies/{_policyId}/rules",
            new { matcher = "DomainSuffix", pattern = "example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RoutingRule created = (await response.Content.ReadFromJsonAsync<RoutingRule>(Json))!;
        Assert.NotEqual(Guid.Empty, created.Id);

        RoutingPolicy policy = _backend.Config.Policies.Single(p => p.Id == _policyId);
        RoutingRule saved = policy.Rules.Single(r => r.Id == created.Id);
        Assert.Equal("example.com", saved.Pattern);
        Assert.Equal(policy.Rules.Max(r => r.Order), saved.Order);
        Assert.Equal(1, _backend.Saves);
    }

    [Fact]
    public async Task A_pattern_that_does_not_parse_is_refused_and_nothing_is_saved()
    {
        int before = _backend.Config.Policies.Single(p => p.Id == _policyId).Rules.Count;

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/policies/{_policyId}/rules",
            new { matcher = "Regex", pattern = "([unclosed" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_PATTERN", await ErrorCode(response));
        Assert.Equal(before, _backend.Config.Policies.Single(p => p.Id == _policyId).Rules.Count);
        Assert.Equal(0, _backend.Saves);
    }

    [Fact]
    public async Task Replacing_the_rules_keeps_the_order_sent()
    {
        HttpResponseMessage response = await _client.PutAsJsonAsync(
            $"/api/policies/{_policyId}/rules",
            new[]
            {
                new { matcher = "DomainSuffix", pattern = "b.com" },
                new { matcher = "DomainSuffix", pattern = "a.com" },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string[] patterns = _backend.Config.Policies.Single(p => p.Id == _policyId)
            .Rules.OrderBy(r => r.Order).Select(r => r.Pattern).ToArray();
        Assert.Equal(new[] { "b.com", "a.com" }, patterns);
    }

    // Normalize puts Default's catch-all back on every save, so an accepted edit would be undone
    // without a word; it has to be refused instead.
    [Fact]
    public async Task The_default_policy_rules_are_refused_rather_than_silently_reverted()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/policies/{RoutingPolicy.DefaultId}/rules",
            new { matcher = "DomainSuffix", pattern = "example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("POLICY_BUILT_IN", await ErrorCode(response));
        Assert.Equal(0, _backend.Saves);
    }

    [Fact]
    public async Task A_body_missing_a_required_field_is_a_400_not_a_500()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/policies/{_policyId}/rules", new { pattern = "example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", await ErrorCode(response));
    }

    // ==== policies ====

    [Fact]
    public async Task Writes_are_refused_while_the_window_has_unsaved_edits()
    {
        _backend.HasPendingChanges = true;

        HttpResponseMessage response = await _client.PutAsJsonAsync(
            $"/api/policies/{RoutingPolicy.DefaultId}/outbound", new { outboundId = Outbound.BlockId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PENDING_CHANGES", await ErrorCode(response));
        Assert.Equal(Outbound.DirectId, _backend.Config.Policies.Single(p => p.Id == RoutingPolicy.DefaultId).OutboundId);
    }

    [Fact]
    public async Task A_policy_can_be_pointed_at_another_outbound_but_not_at_a_missing_one()
    {
        HttpResponseMessage missing = await _client.PutAsJsonAsync(
            $"/api/policies/{RoutingPolicy.DefaultId}/outbound", new { outboundId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("OUTBOUND_NOT_FOUND", await ErrorCode(missing));

        HttpResponseMessage ok = await _client.PutAsJsonAsync(
            $"/api/policies/{RoutingPolicy.DefaultId}/outbound", new { outboundId = Outbound.BlockId });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(Outbound.BlockId, _backend.Config.Policies.Single(p => p.Id == RoutingPolicy.DefaultId).OutboundId);
    }

    [Fact]
    public async Task An_unknown_policy_is_a_404()
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/policies/{Guid.NewGuid()}/rules");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("POLICY_NOT_FOUND", await ErrorCode(response));
    }

    [Fact]
    public async Task A_policy_created_without_an_id_gets_one()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/policies", new
        {
            name = "Games",
            outboundId = Outbound.DirectId,
            rules = new[] { new { matcher = "DomainSuffix", pattern = "steam.com" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RoutingPolicy created = (await response.Content.ReadFromJsonAsync<RoutingPolicy>(Json))!;
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Contains(_backend.Config.Policies, p => p.Id == created.Id && p.Name == "Games");
    }

    [Fact]
    public async Task The_default_policy_cannot_be_deleted()
    {
        HttpResponseMessage response = await _client.DeleteAsync($"/api/policies/{RoutingPolicy.DefaultId}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("POLICY_NOT_REMOVABLE", await ErrorCode(response));
    }

    // ==== process filters ====

    [Fact]
    public async Task A_process_filter_naming_a_missing_policy_is_refused()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/process-rules", new
        {
            name = "chrome",
            policyIds = new[] { Guid.NewGuid() },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("POLICY_NOT_FOUND", await ErrorCode(response));
        Assert.Empty(_backend.Config.ProcessRules);
    }

    [Fact]
    public async Task A_process_filter_with_a_condition_tree_round_trips()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/process-rules", new
        {
            name = "chrome",
            policyIds = new[] { RoutingPolicy.DefaultId },
            condition = new { kind = "process", pattern = "chrome.exe" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ProcessRule saved = Assert.Single(_backend.Config.ProcessRules);
        Assert.Equal("chrome.exe", Assert.IsType<ProxyDivert.Core.Routing.Models.Conditions.ProcessNameCondition>(saved.Condition).Pattern);
        Assert.Equal(new[] { RoutingPolicy.DefaultId }, saved.PolicyIds);
    }

    [Fact]
    public async Task A_process_filter_with_a_pid_that_does_not_parse_is_refused()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/process-rules", new
        {
            name = "emu",
            policyIds = new[] { RoutingPolicy.DefaultId },
            condition = new { kind = "process", matcher = "Pid", pattern = "emu-1" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_PATTERN", await ErrorCode(response));
        Assert.Empty(_backend.Config.ProcessRules);
        Assert.Equal(0, _backend.Saves);
    }

    [Fact]
    public async Task A_process_filter_by_pid_is_saved()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/process-rules", new
        {
            name = "emu",
            policyIds = new[] { RoutingPolicy.DefaultId },
            condition = new { kind = "process", matcher = "Pid", pattern = "4242" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var leaf = Assert.IsType<ProxyDivert.Core.Routing.Models.Conditions.ProcessNameCondition>(
            Assert.Single(_backend.Config.ProcessRules).Condition);
        Assert.Equal(ProxyDivert.Core.Routing.Enums.ProcessMatcherType.Pid, leaf.Matcher);
    }

    // ==== engine ====

    [Fact]
    public async Task The_engine_switch_reaches_the_backend()
    {
        HttpResponseMessage response = await _client.PutAsJsonAsync("/api/engine", new { running = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_backend.EngineRunning);
    }
}
