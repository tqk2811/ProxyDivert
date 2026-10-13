using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProxyDivert.Api;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The MCP server at /mcp over a real Kestrel, driven by the SDK's own client.
public sealed class McpTests : IAsyncLifetime
{
    private readonly FakeApiBackend _backend = new FakeApiBackend();
    private readonly LocalApiSettings _settings = new LocalApiSettings { Enabled = true, Port = 0, McpEnabled = true };
    private readonly LocalApiHost _host;

    public McpTests()
    {
        _host = new LocalApiHost(_backend, () => _settings);
    }

    public async Task InitializeAsync()
    {
        string? error = await _host.ApplyAsync(_settings);
        Assert.True(error is null, error);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<McpClient> ConnectAsync(string? token = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(_host.Address!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        };
        if (token is not null)
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        return McpClient.CreateAsync(new HttpClientTransport(options));
    }

    [Fact]
    public async Task Tools_list_offers_the_api_operations()
    {
        await using McpClient client = await ConnectAsync();
        IList<McpClientTool> tools = await client.ListToolsAsync();
        string[] names = tools.Select(t => t.Name).ToArray();
        foreach (string expected in new[]
                 {
                     "get_status", "list_outbounds", "list_policies", "create_policy", "set_policy_outbound",
                     "add_policy_rule", "replace_policy_rules", "list_process_rules", "create_process_rule",
                     "set_engine_running", "connect_vpn", "disconnect_vpn",
                 })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public async Task A_tool_call_reaches_the_backend()
    {
        await using McpClient client = await ConnectAsync();
        CallToolResult result = await client.CallToolAsync(
            "set_engine_running", new Dictionary<string, object?> { ["running"] = true });
        Assert.NotEqual(true, result.IsError);
        Assert.True(_backend.EngineRunning);
    }

    [Fact]
    public async Task Create_policy_takes_the_api_json_shape()
    {
        await using McpClient client = await ConnectAsync();
        Guid outbound = _backend.Config.Outbounds[0].Id;
        CallToolResult result = await client.CallToolAsync("create_policy", new Dictionary<string, object?>
        {
            ["policy"] = new Dictionary<string, object?> { ["name"] = "From MCP", ["outboundId"] = outbound },
        });
        Assert.True(result.IsError != true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        Assert.Contains(_backend.Config.Policies, p => p.Name == "From MCP");
    }

    [Fact]
    public async Task A_refused_request_is_a_tool_error_with_the_api_code()
    {
        await using McpClient client = await ConnectAsync();
        CallToolResult result = await client.CallToolAsync(
            "get_policy", new Dictionary<string, object?> { ["id"] = Guid.NewGuid() });
        Assert.True(result.IsError);
        string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("NOT_FOUND", text);
    }

    [Fact]
    public async Task With_require_token_mcp_refuses_a_missing_token_and_accepts_the_right_one()
    {
        _settings.RequireToken = true;
        _settings.Token = "secret-token";

        using (var http = new HttpClient())
        {
            HttpResponseMessage bare = await http.PostAsync(new Uri(_host.Address!, "/mcp"), new StringContent("{}"));
            Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
        }

        await using McpClient client = await ConnectAsync("secret-token");
        Assert.NotEmpty(await client.ListToolsAsync());
    }

    [Fact]
    public async Task Mcp_is_not_mapped_while_switched_off()
    {
        _settings.McpEnabled = false;
        string? error = await _host.ApplyAsync(_settings);
        Assert.True(error is null, error);

        using var http = new HttpClient();
        HttpResponseMessage response = await http.PostAsync(new Uri(_host.Address!, "/mcp"), new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
