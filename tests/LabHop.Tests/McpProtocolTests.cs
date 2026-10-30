using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabHop.Auth;
using LabHop.Models;
using LabHop.Services.Projects;
using LabHop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LabHop.Tests;

/// <summary>
/// The MCP endpoint at the HTTP boundary and through the SDK's real client: criteria M1-M11. Tokens come from
/// the real OAuth flow, never from test-only shortcuts.
/// </summary>
public sealed class McpProtocolTests(LabHopAppFixture fixture) : IClassFixture<LabHopAppFixture>, IAsyncLifetime
{
    private const string ListProjectsCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_projects","arguments":{}}}""";

    private readonly LabHopApp app = fixture.App;
    private ProjectService service = null!;
    private TestUser alice = null!;
    private TestUser bob = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        service = ActivatorUtilities.CreateInstance<ProjectService>(app.Services);
        alice = await app.CreateUserAsync();
        bob = await app.CreateUserAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static HttpRequestMessage Rpc(string body, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private async Task<McpClient> ConnectAsync(string accessToken)
    {
        var http = app.CreateProtocolClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = app.McpEndpoint, TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static void AssertProtocolRejection(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    private static string Challenge(HttpResponseMessage response) =>
        string.Join(", ", response.Headers.WwwAuthenticate.Select(header => header.ToString()));

    [Fact]
    public async Task Missing_token_gets_401_with_discovery_information_and_no_login_redirect()
    {
        var response = await app.CreateProtocolClient().SendAsync(Rpc(ListProjectsCall), Ct);

        AssertProtocolRejection(response, HttpStatusCode.Unauthorized);
        var challenge = Challenge(response);
        Assert.StartsWith("Bearer", challenge);
        Assert.Contains("resource_metadata=\"https://localhost/.well-known/oauth-protected-resource/mcp\"", challenge);
        Assert.Contains($"scope=\"{LabHopScopes.Read} {LabHopScopes.Write}\"", challenge);
        Assert.DoesNotContain("error=", challenge);
    }

    [Fact]
    public async Task Get_without_a_token_also_gets_401()
    {
        var response = await app.CreateProtocolClient().GetAsync("/mcp", Ct);

        AssertProtocolRejection(response, HttpStatusCode.Unauthorized);
        Assert.Contains("resource_metadata=", Challenge(response));
    }

    [Fact]
    public async Task Get_with_a_token_gets_405_because_the_server_is_stateless()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);
        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await app.CreateProtocolClient().SendAsync(request, Ct);

        AssertProtocolRejection(response, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Unknown_path_under_mcp_is_404_rather_than_a_login_redirect()
    {
        var response = await app.CreateProtocolClient().GetAsync("/mcp/does-not-exist", Ct);

        AssertProtocolRejection(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Access_token_in_a_form_body_is_ignored()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["access_token"] = tokens.AccessToken });

        var response = await app.CreateProtocolClient().PostAsync("/mcp", form, Ct);

        AssertProtocolRejection(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/")]
    [InlineData("/MCP")]
    public async Task Path_variations_cannot_skip_the_origin_check(string path)
    {
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);
        var request = Rpc(ListProjectsCall, tokens.AccessToken);
        request.RequestUri = new Uri(path, UriKind.Relative);
        request.Headers.Add("Origin", "https://evil.example.net");

        var response = await app.CreateProtocolClient().SendAsync(request, Ct);

        AssertProtocolRejection(response, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/mcp/")]
    [InlineData("/MCP")]
    public async Task Path_variations_still_get_the_scope_challenge(string path)
    {
        var project = await service.CreateAsync(alice.AsHuman, new NewProject("NAS rebuild"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);
        var call = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "update_project", arguments = new { projectId = project.Id, expectedVersion = project.Version, name = "Hijacked" } },
        });
        var request = Rpc(call, tokens.AccessToken);
        request.RequestUri = new Uri(path, UriKind.Relative);

        var response = await app.CreateProtocolClient().SendAsync(request, Ct);

        AssertProtocolRejection(response, HttpStatusCode.Forbidden);
        Assert.Contains("error=\"insufficient_scope\"", Challenge(response));
        Assert.Equal("NAS rebuild", (await service.GetAsync(alice.AsHuman, project.Id, Ct)).Name);
    }

    [Fact]
    public async Task A_valid_browser_cookie_does_not_authenticate_mcp()
    {
        var driver = new OAuthDriver(app);
        await driver.SignInAsync(alice);
        Assert.Equal(HttpStatusCode.OK, (await driver.Browser.GetAsync("/projects", Ct)).StatusCode);

        var response = await driver.Browser.SendAsync(Rpc(ListProjectsCall), Ct);

        AssertProtocolRejection(response, HttpStatusCode.Unauthorized);
        Assert.Contains("resource_metadata=", Challenge(response));
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiJhZG1pbiJ9.")]
    public async Task Invalid_token_gets_401_invalid_token(string token)
    {
        var response = await app.CreateProtocolClient().SendAsync(Rpc(ListProjectsCall, token), Ct);

        AssertProtocolRejection(response, HttpStatusCode.Unauthorized);
        Assert.Contains("error=\"invalid_token\"", Challenge(response));
    }

    [Fact]
    public async Task Access_token_in_the_query_string_is_ignored()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);
        var request = Rpc(ListProjectsCall);
        request.RequestUri = new Uri($"/mcp?access_token={tokens.AccessToken}", UriKind.Relative);

        var response = await app.CreateProtocolClient().SendAsync(request, Ct);

        AssertProtocolRejection(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Disallowed_origin_gets_403()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);
        var request = Rpc(ListProjectsCall, tokens.AccessToken);
        request.Headers.Add("Origin", "https://evil.example.net");

        var response = await app.CreateProtocolClient().SendAsync(request, Ct);

        AssertProtocolRejection(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Read_only_token_can_read_but_not_see_the_write_tool()
    {
        var project = await service.CreateAsync(alice.AsHuman, new NewProject("NAS rebuild", Goal: "More storage"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);

        await using var client = await ConnectAsync(tokens.AccessToken);

        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Select(tool => tool.Name).ToList();
        Assert.Contains("list_projects", tools);
        Assert.Contains("get_project_context", tools);
        Assert.DoesNotContain("update_project", tools);

        var list = await client.CallToolAsync("list_projects", cancellationToken: Ct);
        Assert.NotEqual(true, list.IsError);
        Assert.Contains(project.Id.ToString(), TextOf(list));

        var context = await client.CallToolAsync("get_project_context", new Dictionary<string, object?> { ["projectId"] = project.Id }, cancellationToken: Ct);
        Assert.NotEqual(true, context.IsError);
        Assert.Contains("More storage", TextOf(context));
    }

    [Fact]
    public async Task Read_only_token_calling_the_write_tool_gets_403_with_a_scope_challenge()
    {
        var project = await service.CreateAsync(alice.AsHuman, new NewProject("NAS rebuild"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);
        var call = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "update_project", arguments = new { projectId = project.Id, expectedVersion = project.Version, name = "Hijacked" } },
        });

        var response = await app.CreateProtocolClient().SendAsync(Rpc(call, tokens.AccessToken), Ct);

        AssertProtocolRejection(response, HttpStatusCode.Forbidden);
        var challenge = Challenge(response);
        Assert.Contains("error=\"insufficient_scope\"", challenge);
        Assert.Contains($"scope=\"{LabHopScopes.Read} {LabHopScopes.Write}\"", challenge);
        Assert.Contains("resource_metadata=", challenge);
        Assert.Equal("NAS rebuild", (await service.GetAsync(alice.AsHuman, project.Id, Ct)).Name);
    }

    /// <summary>
    /// JSON-RPC shapes a pre-SDK check could read differently from the SDK. Whatever the HTTP answer, the write
    /// must not happen, because the tool's own authorization is the real check.
    /// </summary>
    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_projects","name":"update_project","arguments":{"projectId":"__ID__","expectedVersion":__V__,"name":"Hijacked"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"update_project","name":"list_projects","arguments":{"projectId":"__ID__","expectedVersion":__V__,"name":"Hijacked"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","method":"tools/call","params":{"name":"update_project","arguments":{"projectId":"__ID__","expectedVersion":__V__,"name":"Hijacked"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","method":"tools/list","params":{"name":"update_project","arguments":{"projectId":"__ID__","expectedVersion":__V__,"name":"Hijacked"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"Method":"tools/call","Params":{"Name":"update_project","Arguments":{"projectId":"__ID__","expectedVersion":__V__,"name":"Hijacked"}}}""")]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"update_project","arguments":{"projectId":"__ID__","expectedVersion":__V__,"name":"Hijacked"}}}]""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"Name":"update_project","Arguments":{"projectId":"__ID__","expectedVersion":__V__,"name":"Hijacked"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"NAME":"update_project","arguments":{"projectId":"__ID__","expectedVersion":__V__,"name":"Hijacked"}},}""")]
    public async Task Payload_tricks_cannot_reach_the_write_tool_without_the_scope(string template)
    {
        var project = await service.CreateAsync(alice.AsHuman, new NewProject("NAS rebuild"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);
        var body = template.Replace("__ID__", project.Id.ToString()).Replace("__V__", project.Version.ToString());

        await app.CreateProtocolClient().SendAsync(Rpc(body, tokens.AccessToken), Ct);

        var current = await service.GetAsync(alice.AsHuman, project.Id, Ct);
        Assert.Equal("NAS rebuild", current.Name);
        Assert.Equal(project.Version, current.Version);
    }

    [Fact]
    public async Task Write_token_updates_through_the_shared_rules_and_is_recorded_as_an_agent()
    {
        var project = await service.CreateAsync(alice.AsHuman, new NewProject("NAS rebuild"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: true);
        await using var client = await ConnectAsync(tokens.AccessToken);

        var result = await client.CallToolAsync(
            "update_project",
            new Dictionary<string, object?> { ["projectId"] = project.Id, ["expectedVersion"] = project.Version, ["goal"] = "Set by agent", ["status"] = "Active" },
            cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var current = await service.GetAsync(alice.AsHuman, project.Id, Ct);
        Assert.Equal("Set by agent", current.Goal);
        Assert.Equal(ProjectStatus.Active, current.Status);

        await using var db = app.CreateDb();
        var entry = await db.ActivityEntries.AsNoTracking()
            .Where(a => a.ProjectId == project.Id && a.Action == ActivityActions.ProjectUpdated)
            .SingleAsync(Ct);
        Assert.Equal(ActorKind.Agent, entry.ActorKind);
        Assert.Equal(alice.Id, entry.ActorUserId);
        Assert.Equal(LabHopApp.LoopbackClientId, entry.ClientId);
        Assert.Equal(LabHopApp.LoopbackClientName, entry.ActorDisplayName);
    }

    [Fact]
    public async Task Stale_version_returns_an_actionable_error_and_saves_nothing()
    {
        var original = await service.CreateAsync(alice.AsHuman, new NewProject("NAS rebuild"), Ct);
        await service.UpdateAsync(alice.AsHuman, original.Id, original.Version, new ProjectChanges(Goal: "Newer"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: true);
        await using var client = await ConnectAsync(tokens.AccessToken);

        var result = await client.CallToolAsync(
            "update_project",
            new Dictionary<string, object?> { ["projectId"] = original.Id, ["expectedVersion"] = original.Version, ["goal"] = "Stale" },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("get_project_context", TextOf(result));
        Assert.Equal("Newer", (await service.GetAsync(alice.AsHuman, original.Id, Ct)).Goal);
    }

    [Fact]
    public async Task Validation_errors_from_the_shared_rules_reach_the_agent()
    {
        var project = await service.CreateAsync(alice.AsHuman, new NewProject("NAS rebuild"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: true);
        await using var client = await ConnectAsync(tokens.AccessToken);

        var result = await client.CallToolAsync(
            "update_project",
            new Dictionary<string, object?> { ["projectId"] = project.Id, ["expectedVersion"] = project.Version, ["name"] = "   " },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("Name is required", TextOf(result));
        Assert.Equal("NAS rebuild", (await service.GetAsync(alice.AsHuman, project.Id, Ct)).Name);
    }

    [Fact]
    public async Task One_users_token_never_reaches_another_users_project()
    {
        var alicesProject = await service.CreateAsync(alice.AsHuman, new NewProject("Alice private project"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, bob, grantWrite: true);
        await using var client = await ConnectAsync(tokens.AccessToken);

        var list = await client.CallToolAsync("list_projects", new Dictionary<string, object?> { ["includeArchived"] = true }, cancellationToken: Ct);
        Assert.DoesNotContain("Alice private project", TextOf(list));

        var context = await client.CallToolAsync("get_project_context", new Dictionary<string, object?> { ["projectId"] = alicesProject.Id }, cancellationToken: Ct);
        Assert.True(context.IsError);
        Assert.DoesNotContain("Alice private project", TextOf(context));

        var update = await client.CallToolAsync(
            "update_project",
            new Dictionary<string, object?> { ["projectId"] = alicesProject.Id, ["expectedVersion"] = alicesProject.Version, ["name"] = "Taken" },
            cancellationToken: Ct);
        Assert.True(update.IsError);
        Assert.Equal("Alice private project", (await service.GetAsync(alice.AsHuman, alicesProject.Id, Ct)).Name);
    }

    [Fact]
    public async Task A_user_id_argument_cannot_change_whose_data_is_returned()
    {
        await service.CreateAsync(alice.AsHuman, new NewProject("Alice private project"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, bob, grantWrite: false);
        await using var client = await ConnectAsync(tokens.AccessToken);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        foreach (var tool in tools)
        {
            var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToList();
            Assert.DoesNotContain(properties, name => name.Contains("user", StringComparison.OrdinalIgnoreCase) || name.Contains("owner", StringComparison.OrdinalIgnoreCase));
        }

        var spoof = new Dictionary<string, object?> { ["userId"] = alice.Id, ["ownerId"] = alice.Id, ["sub"] = alice.Id, ["user"] = alice.Id };
        try
        {
            var result = await client.CallToolAsync("list_projects", spoof, cancellationToken: Ct);
            Assert.DoesNotContain("Alice private project", TextOf(result));
        }
        catch (ModelContextProtocol.McpException)
        {
            // Rejecting unknown arguments outright is also correct.
        }
    }

    [Fact]
    public async Task Tool_results_contain_no_owner_or_user_identifiers()
    {
        var project = await service.CreateAsync(alice.AsHuman, new NewProject("NAS rebuild"), Ct);
        var tokens = await OAuthDriver.ConnectAsync(app, alice, grantWrite: false);
        await using var client = await ConnectAsync(tokens.AccessToken);

        var list = TextOf(await client.CallToolAsync("list_projects", cancellationToken: Ct));
        var context = TextOf(await client.CallToolAsync("get_project_context", new Dictionary<string, object?> { ["projectId"] = project.Id }, cancellationToken: Ct));

        Assert.DoesNotContain(alice.Id, list);
        Assert.DoesNotContain(alice.Id, context);
        Assert.DoesNotContain("ownerId", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerId", context, StringComparison.OrdinalIgnoreCase);
    }
}
