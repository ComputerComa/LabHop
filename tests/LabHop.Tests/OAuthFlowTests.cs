using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using LabHop.Auth;
using LabHop.Data;
using LabHop.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit;

namespace LabHop.Tests;

/// <summary>The authorization server through its public HTTP surface: criteria O1-O13.</summary>
public sealed class OAuthFlowTests(PostgresServer postgres, ZeroReuseLeewayAppFixture fixture) : IClassFixture<ZeroReuseLeewayAppFixture>, IAsyncLifetime
{
    private const string ListProjectsCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_projects","arguments":{}}}""";

    private readonly LabHopApp app = fixture.App;
    private readonly OAuthDriver driver = new(fixture.App);
    private TestUser user = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => user = await app.CreateUserAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<HttpStatusCode> CallMcpAsync(LabHopApp target, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(ListProjectsCall, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (await target.CreateProtocolClient().SendAsync(request, Ct)).StatusCode;
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await app.CreateProtocolClient().GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.Clone();
    }

    private static List<string?> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(item => item.GetString()).ToList();

    private static void AssertRedirectedWithError(HttpResponseMessage response, string expectedError)
    {
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal(expectedError, query["error"].ToString());
        Assert.False(query.ContainsKey("code"));
    }

    /// <summary>
    /// OpenIddict reports authorization requests that fail validation on its own error page rather than by redirect.
    /// The first version of these tests assumed a redirect; what the requirements need is a rejection that names the
    /// error, sends nothing to the client's redirect URI, and issues no code.
    /// </summary>
    private static async Task AssertRejectedWithoutRedirect(HttpResponseMessage response, string expectedError)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains(expectedError, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task Protected_resource_metadata_names_the_resource_and_issuer(string path)
    {
        var metadata = await GetJsonAsync(path);
        var authorizationServer = await GetJsonAsync("/.well-known/oauth-authorization-server");

        Assert.Equal("https://localhost/mcp", metadata.GetProperty("resource").GetString());
        var issuer = Assert.Single(Strings(metadata, "authorization_servers"));
        Assert.Equal(authorizationServer.GetProperty("issuer").GetString(), issuer);
        Assert.Contains(LabHopScopes.Read, Strings(metadata, "scopes_supported"));
        Assert.Contains(LabHopScopes.Write, Strings(metadata, "scopes_supported"));
    }

    [Theory]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/openid-configuration")]
    public async Task Authorization_server_metadata_advertises_only_what_is_supported(string path)
    {
        var metadata = await GetJsonAsync(path);

        Assert.Equal(["S256"], Strings(metadata, "code_challenge_methods_supported"));
        Assert.Equal(["authorization_code", "refresh_token"], Strings(metadata, "grant_types_supported").Order().ToList());
        Assert.Equal(["code"], Strings(metadata, "response_types_supported"));
        Assert.Equal(["none"], Strings(metadata, "token_endpoint_auth_methods_supported"));
        Assert.Equal(["labhop.read", "labhop.write", "offline_access"], Strings(metadata, "scopes_supported").Order().ToList());
        Assert.Equal(["consent", "none"], Strings(metadata, "prompt_values_supported").Order().ToList());
        Assert.False(metadata.TryGetProperty("registration_endpoint", out _));
        Assert.True(metadata.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.StartsWith("https://localhost/", metadata.GetProperty("authorization_endpoint").GetString());
        Assert.StartsWith("https://localhost/", metadata.GetProperty("token_endpoint").GetString());
        Assert.StartsWith("https://localhost/", metadata.GetProperty("revocation_endpoint").GetString());
    }

    [Theory]
    [InlineData("/.well-known/does-not-exist")]
    [InlineData("/connect/does-not-exist")]
    public async Task Unknown_protocol_paths_are_404_rather_than_a_login_redirect(string path)
    {
        var response = await app.CreateProtocolClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Authorization_request_for_an_anonymous_user_goes_to_login_and_returns_after()
    {
        var request = driver.NewRequest();

        var anonymous = await driver.Browser.GetAsync(request.Query(), Ct);
        Assert.Equal(HttpStatusCode.Found, anonymous.StatusCode);
        var login = anonymous.Headers.Location!;
        Assert.Contains("/Account/Login", login.ToString());

        var returnUrl = QueryHelpers.ParseQuery(login.Query)["ReturnUrl"].ToString();
        var signedIn = await driver.SubmitLoginAsync(user.Email, user.Password, returnUrl);

        Assert.Equal(HttpStatusCode.Found, signedIn.StatusCode);
        var consent = await driver.Browser.GetAsync(signedIn.Headers.Location, Ct);
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        Assert.Contains(LabHopApp.LoopbackClientName, await consent.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Code_flow_with_pkce_yields_tokens_that_work_and_names_the_issuer()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest();
        var consent = await driver.OpenConsentAsync(request);

        var decision = await driver.DecideAsync(consent, approve: true, grantWrite: true);
        Assert.Equal(HttpStatusCode.Found, decision.StatusCode);
        var query = QueryHelpers.ParseQuery(decision.Headers.Location!.Query);
        Assert.Equal(request.State, query["state"].ToString());
        Assert.Equal("https://localhost/", query["iss"].ToString());

        var tokens = await OAuthDriver.ReadTokensAsync(await driver.RedeemAsync(request, query["code"].ToString()));
        Assert.NotNull(tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, await CallMcpAsync(app, tokens.AccessToken));
    }

    [Fact]
    public async Task Missing_pkce_is_rejected()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest() with { ChallengeMethod = null };

        var response = await driver.Browser.GetAsync(request.Query(), Ct);

        await AssertRejectedWithoutRedirect(response, "invalid_request");
    }

    [Fact]
    public async Task Plain_pkce_is_rejected()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest();
        request = request with { ChallengeMethod = "plain", CodeChallenge = request.CodeVerifier };

        var response = await driver.Browser.GetAsync(request.Query(), Ct);

        await AssertRejectedWithoutRedirect(response, "invalid_request");
    }

    [Theory]
    [InlineData("openid labhop.read")]
    [InlineData("labhop.admin")]
    public async Task Scopes_the_server_does_not_offer_are_rejected(string scope)
    {
        await driver.SignInAsync(user);

        var response = await driver.Browser.GetAsync(driver.NewRequest(scope: scope).Query(), Ct);

        await AssertRejectedWithoutRedirect(response, "invalid_scope");
    }

    [Fact]
    public async Task Forced_login_prompt_is_rejected_because_it_is_not_implemented()
    {
        await driver.SignInAsync(user);

        var response = await driver.Browser.GetAsync(driver.NewRequest().Query() + "&prompt=login", Ct);

        await AssertRejectedWithoutRedirect(response, "invalid_request");
    }

    [Fact]
    public async Task Wrong_verifier_is_rejected()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest();
        var code = await driver.ObtainCodeAsync(request, grantWrite: false);

        var response = await driver.RedeemAsync(request, code, verifier: driver.NewRequest().CodeVerifier);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", await OAuthDriver.ReadErrorAsync(response));
    }

    [Fact]
    public async Task Reused_code_is_rejected_and_revokes_what_it_issued()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest();
        var code = await driver.ObtainCodeAsync(request, grantWrite: false);
        var tokens = await OAuthDriver.ReadTokensAsync(await driver.RedeemAsync(request, code));

        var replay = await driver.RedeemAsync(request, code);

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", await OAuthDriver.ReadErrorAsync(replay));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallMcpAsync(app, tokens.AccessToken));
    }

    [Fact]
    public async Task Unknown_client_is_rejected_without_redirecting()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest(clientId: "nobody");

        var response = await driver.Browser.GetAsync(request.Query(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData(LabHopApp.LoopbackClientId, "http://localhost:1234/callback", true)]
    [InlineData(LabHopApp.LoopbackClientId, "http://localhost:65000/callback", true)]
    [InlineData(LabHopApp.LoopbackClientId, "http://127.0.0.1:1234/callback", true)]
    [InlineData(LabHopApp.LoopbackClientId, "http://[::1]:1234/callback", true)]
    [InlineData(LabHopApp.LoopbackClientId, "http://localhost:1234/other", false)]
    [InlineData(LabHopApp.LoopbackClientId, "http://evil.example.net:1234/callback", false)]
    [InlineData(LabHopApp.LoopbackClientId, "https://localhost:1234/callback", false)]
    [InlineData(LabHopApp.LoopbackClientId, "http://localhost.evil.example.net:1234/callback", false)]
    [InlineData(LabHopApp.HostedClientId, LabHopApp.HostedRedirect, true)]
    [InlineData(LabHopApp.HostedClientId, "https://agent.example.net:8443/oauth/callback", false)]
    [InlineData(LabHopApp.HostedClientId, "https://agent.example.net/oauth/callback/extra", false)]
    [InlineData(LabHopApp.HostedClientId, "http://localhost:1234/callback", false)]
    public async Task Redirect_uris_match_only_as_registered(string clientId, string redirectUri, bool accepted)
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest(clientId, redirectUri);

        var response = await driver.Browser.GetAsync(request.Query(), Ct);

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
    }

    [Fact]
    public async Task Resource_other_than_the_mcp_endpoint_is_rejected_at_authorization()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest() with { Resource = "https://other.example.net/mcp" };

        var response = await driver.Browser.GetAsync(request.Query(), Ct);

        await AssertRejectedWithoutRedirect(response, "invalid_target");
    }

    [Fact]
    public async Task Resource_other_than_the_mcp_endpoint_is_rejected_at_the_token_endpoint()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest();
        var code = await driver.ObtainCodeAsync(request, grantWrite: false);

        var response = await driver.RedeemAsync(request, code, resource: "https://other.example.net/mcp");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_target", await OAuthDriver.ReadErrorAsync(response));
    }

    [Fact]
    public async Task Omitted_resource_still_produces_a_token_bound_to_the_mcp_endpoint()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest() with { Resource = null };
        var code = await driver.ObtainCodeAsync(request, grantWrite: false);
        var tokens = await OAuthDriver.ReadTokensAsync(await driver.RedeemAsync(request, code));

        Assert.Equal(HttpStatusCode.OK, await CallMcpAsync(app, tokens.AccessToken));
    }

    [Fact]
    public async Task A_token_from_another_labhop_instance_is_rejected()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, user, grantWrite: true);

        // Same database and keys, different public URL: the token's issuer and audience are not this server's.
        await using var other = new LabHopApp(app.ConnectionString, app.KeysPath, "https://other.example.net/");

        Assert.Equal(HttpStatusCode.Unauthorized, await CallMcpAsync(other, tokens.AccessToken));
    }

    [Fact]
    public async Task A_token_from_this_issuer_for_another_resource_is_rejected()
    {
        // Issuer, signature, scopes, and database entry are all valid here, so only the audience check can reject it.
        await using var misissuing = new LabHopApp(app.ConnectionString, app.KeysPath)
        {
            ConfigureTestServices = services => services.AddOpenIddict().AddServer(server =>
                server.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        context.AccessTokenPrincipal?.SetAudiences("https://other.example.net/mcp");
                        return default;
                    })
                    .SetOrder(OpenIddictServerHandlers.PrepareAccessTokenPrincipal.Descriptor.Order + 1)
                    .SetType(OpenIddictServerHandlerType.Custom))),
        };

        var tokens = await OAuthDriver.ConnectAsync(misissuing, user, grantWrite: false);

        Assert.Equal(HttpStatusCode.Unauthorized, await CallMcpAsync(misissuing, tokens.AccessToken));
    }

    [Fact]
    public async Task Consent_page_shows_the_client_the_redirect_host_and_a_loopback_warning()
    {
        await driver.SignInAsync(user);

        var loopback = await driver.Browser.GetAsync(driver.NewRequest().Query(), Ct);
        var loopbackHtml = await loopback.Content.ReadAsStringAsync(Ct);
        Assert.Contains(LabHopApp.LoopbackClientName, loopbackHtml);
        Assert.Contains("<strong>localhost</strong>", loopbackHtml);
        Assert.Contains("on your own computer", loopbackHtml);
        Assert.Contains("no-store", loopback.Headers.CacheControl?.ToString());
        Assert.Contains("frame-ancestors 'none'", string.Join(' ', loopback.Headers.GetValues("Content-Security-Policy")));

        var hosted = await driver.Browser.GetAsync(driver.NewRequest(LabHopApp.HostedClientId, LabHopApp.HostedRedirect).Query(), Ct);
        var hostedHtml = await hosted.Content.ReadAsStringAsync(Ct);
        Assert.Contains("<strong>agent.example.net</strong>", hostedHtml);
        Assert.DoesNotContain("on your own computer", hostedHtml);
    }

    [Fact]
    public async Task Consent_approval_needs_a_valid_antiforgery_token()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest();
        var fields = await driver.OpenConsentAsync(request);
        var withoutToken = fields.Where(field => !field.Key.Contains("RequestVerificationToken")).ToDictionary();

        var forged = await driver.DecideAsync(withoutToken, approve: true, grantWrite: true);

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Null(forged.Headers.Location);
    }

    [Fact]
    public async Task Consent_cannot_be_approved_from_a_different_browser_session()
    {
        await driver.SignInAsync(user);
        var fields = await driver.OpenConsentAsync(driver.NewRequest());

        var anotherBrowser = new OAuthDriver(app);
        var response = await anotherBrowser.DecideAsync(fields, approve: true, grantWrite: true);

        Assert.False(
            response.Headers.Location?.Query.Contains("code=", StringComparison.Ordinal) == true,
            "A different browser obtained an authorization code.");
    }

    [Fact]
    public async Task Prompt_none_never_skips_consent()
    {
        await driver.SignInAsync(user);
        await driver.ObtainCodeAsync(driver.NewRequest(), grantWrite: false);

        var response = await driver.Browser.GetAsync(driver.NewRequest().Query() + "&prompt=none", Ct);

        AssertRedirectedWithError(response, "consent_required");
    }

    [Fact]
    public async Task Denying_consent_returns_access_denied_and_issues_nothing()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest();
        var fields = await driver.OpenConsentAsync(request);

        var response = await driver.DecideAsync(fields, approve: false, grantWrite: false);

        AssertRedirectedWithError(response, "access_denied");
        await using var scope = app.Services.CreateAsyncScope();
        var connections = await scope.ServiceProvider.GetRequiredService<AgentAccessService>().ListAsync(user.AsHuman, Ct);
        Assert.Empty(connections);
    }

    [Fact]
    public async Task Withheld_write_access_stays_withheld_through_a_refresh()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, user, grantWrite: false);
        Assert.DoesNotContain(LabHopScopes.Write, tokens.Scope ?? "");

        var widened = await driver.RefreshAsync(LabHopApp.LoopbackClientId, tokens.RefreshToken!, $"{LabHopScopes.Read} {LabHopScopes.Write}");

        if (widened.IsSuccessStatusCode)
        {
            var refreshed = await OAuthDriver.ReadTokensAsync(widened);
            Assert.DoesNotContain(LabHopScopes.Write, refreshed.Scope ?? "");
        }
        else
        {
            Assert.Contains(await OAuthDriver.ReadErrorAsync(widened), new[] { "invalid_scope", "invalid_grant" });
        }
    }

    [Fact]
    public async Task Write_scope_is_not_granted_when_the_client_did_not_request_it()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, user, grantWrite: true, scope: $"{LabHopScopes.Read} offline_access");

        Assert.DoesNotContain(LabHopScopes.Write, tokens.Scope ?? "");
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_a_replayed_one_is_rejected()
    {
        var first = await OAuthDriver.ConnectAsync(app, user, grantWrite: false);

        var second = await OAuthDriver.ReadTokensAsync(await driver.RefreshAsync(LabHopApp.LoopbackClientId, first.RefreshToken!));
        Assert.NotNull(second.RefreshToken);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, await CallMcpAsync(app, second.AccessToken));

        var replay = await driver.RefreshAsync(LabHopApp.LoopbackClientId, first.RefreshToken!);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", await OAuthDriver.ReadErrorAsync(replay));
    }

    [Fact]
    public async Task Refresh_is_refused_for_a_locked_out_user()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, user, grantWrite: false);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await users.SetLockoutEndDateAsync((await users.FindByIdAsync(user.Id))!, DateTimeOffset.UtcNow.AddHours(1));
        }

        var response = await driver.RefreshAsync(LabHopApp.LoopbackClientId, tokens.RefreshToken!);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", await OAuthDriver.ReadErrorAsync(response));
    }

    [Fact]
    public async Task Revoking_a_connection_stops_access_and_refresh_tokens_immediately()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, user, grantWrite: true);
        Assert.Equal(HttpStatusCode.OK, await CallMcpAsync(app, tokens.AccessToken));

        await using var scope = app.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<AgentAccessService>();
        var connection = Assert.Single(await access.ListAsync(user.AsHuman, Ct));
        Assert.Equal(LabHopApp.LoopbackClientName, connection.ClientName);
        Assert.Contains(LabHopScopes.Write, connection.Scopes);

        await access.RevokeAsync(user.AsHuman, connection.AuthorizationId, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, await CallMcpAsync(app, tokens.AccessToken));
        var refresh = await driver.RefreshAsync(LabHopApp.LoopbackClientId, tokens.RefreshToken!);
        Assert.Equal("invalid_grant", await OAuthDriver.ReadErrorAsync(refresh));
        Assert.Empty(await access.ListAsync(user.AsHuman, Ct));
    }

    [Fact]
    public async Task Users_can_only_see_and_revoke_their_own_connections()
    {
        var tokens = await OAuthDriver.ConnectAsync(app, user, grantWrite: false);
        var other = await app.CreateUserAsync();

        await using var scope = app.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<AgentAccessService>();
        var connection = Assert.Single(await access.ListAsync(user.AsHuman, Ct));

        Assert.Empty(await access.ListAsync(other.AsHuman, Ct));
        await Assert.ThrowsAsync<LabHop.Services.RuleViolationException>(() => access.RevokeAsync(other.AsHuman, connection.AuthorizationId, Ct));
        Assert.Equal(HttpStatusCode.OK, await CallMcpAsync(app, tokens.AccessToken));
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    public async Task Revocation_endpoint_stops_the_presented_token(string hint)
    {
        var tokens = await OAuthDriver.ConnectAsync(app, user, grantWrite: false);
        var token = hint == "access_token" ? tokens.AccessToken : tokens.RefreshToken!;

        var response = await driver.Client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = LabHopApp.LoopbackClientId, ["token"] = token, ["token_type_hint"] = hint }),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (hint == "access_token")
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await CallMcpAsync(app, tokens.AccessToken));
        }
        else
        {
            var refresh = await driver.RefreshAsync(LabHopApp.LoopbackClientId, tokens.RefreshToken!);
            Assert.Equal("invalid_grant", await OAuthDriver.ReadErrorAsync(refresh));
        }
    }

    [Fact]
    public async Task Tokens_and_browser_sessions_survive_a_restart()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await LabHopApp.MigrateAsync(connectionString);
        var keysPath = LabHopApp.CreateKeysPath();

        TokenSet tokens;
        string sessionCookie;
        await using (var first = new LabHopApp(connectionString, keysPath))
        {
            _ = first.Server;
            await first.RegisterClientsAsync();
            var account = await first.CreateUserAsync();
            tokens = await OAuthDriver.ConnectAsync(first, account, grantWrite: false);

            var login = await new OAuthDriver(first).SubmitLoginAsync(account.Email, account.Password);
            sessionCookie = login.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal)).Split(';')[0];
        }

        await using var restarted = new LabHopApp(connectionString, keysPath);

        Assert.Equal(HttpStatusCode.OK, await CallMcpAsync(restarted, tokens.AccessToken));

        var refreshed = await new OAuthDriver(restarted).RefreshAsync(LabHopApp.LoopbackClientId, tokens.RefreshToken!);
        Assert.True(refreshed.IsSuccessStatusCode);

        var page = new HttpRequestMessage(HttpMethod.Get, "/projects");
        page.Headers.Add("Cookie", sessionCookie);
        Assert.Equal(HttpStatusCode.OK, (await restarted.CreateProtocolClient().SendAsync(page, Ct)).StatusCode);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Key_files_are_readable_only_by_the_service_account()
    {
        var certificates = Directory.EnumerateFiles(app.KeysPath, "*.pfx").ToList();
        Assert.NotEmpty(certificates);
        foreach (var file in certificates)
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }

        var dataProtectionKeys = Directory.EnumerateFiles(Path.Combine(app.KeysPath, "data-protection")).ToList();
        Assert.NotEmpty(dataProtectionKeys);
        foreach (var file in dataProtectionKeys)
        {
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
        }
    }

    [Fact]
    public async Task Secrets_do_not_appear_in_logs_during_the_flow()
    {
        await driver.SignInAsync(user);
        var request = driver.NewRequest();
        var code = await driver.ObtainCodeAsync(request, grantWrite: true);
        var tokens = await OAuthDriver.ReadTokensAsync(await driver.RedeemAsync(request, code));
        await CallMcpAsync(app, tokens.AccessToken);
        var refreshed = await OAuthDriver.ReadTokensAsync(await driver.RefreshAsync(LabHopApp.LoopbackClientId, tokens.RefreshToken!));

        var logs = app.Logs.AllText;
        Assert.NotEmpty(logs);
        Assert.DoesNotContain(user.Password, logs);
        Assert.DoesNotContain(code, logs);
        Assert.DoesNotContain(request.CodeVerifier, logs);
        Assert.DoesNotContain(tokens.AccessToken, logs);
        Assert.DoesNotContain(tokens.RefreshToken!, logs);
        Assert.DoesNotContain(refreshed.AccessToken, logs);
        Assert.DoesNotContain(refreshed.RefreshToken!, logs);
    }
}
