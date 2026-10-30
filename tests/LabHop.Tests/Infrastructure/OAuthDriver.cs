using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LabHop.Auth;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace LabHop.Tests.Infrastructure;

public sealed record TokenSet(string AccessToken, string? RefreshToken, string? Scope);

public sealed record AuthorizationRequest(
    string ClientId,
    string RedirectUri,
    string Scope,
    string CodeChallenge,
    string CodeVerifier,
    string State)
{
    public string? Resource { get; init; }
    public string? ChallengeMethod { get; init; } = "S256";

    public string Query()
    {
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["state"] = State,
        };
        if (ChallengeMethod is not null)
        {
            parameters["code_challenge"] = CodeChallenge;
            parameters["code_challenge_method"] = ChallengeMethod;
        }

        if (Resource is not null)
        {
            parameters["resource"] = Resource;
        }

        return QueryHelpers.AddQueryString("/connect/authorize", parameters);
    }
}

/// <summary>Plays the parts of the user's browser and an OAuth client, using only the public HTTP surface.</summary>
public sealed partial class OAuthDriver(LabHopApp app)
{
    public const string AllScopes = $"{LabHopScopes.Read} {LabHopScopes.Write} offline_access";
    public const string LoopbackRedirect = "http://localhost:49152/callback";

    public HttpClient Browser { get; } = app.CreateBrowser();
    public HttpClient Client { get; } = app.CreateProtocolClient();

    public AuthorizationRequest NewRequest(
        string clientId = LabHopApp.LoopbackClientId,
        string redirectUri = LoopbackRedirect,
        string scope = AllScopes)
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new AuthorizationRequest(clientId, redirectUri, scope, challenge, verifier, Guid.NewGuid().ToString("N"))
        {
            Resource = app.McpEndpoint.AbsoluteUri,
        };
    }

    public async Task SignInAsync(TestUser user)
    {
        var response = await SubmitLoginAsync(user.Email, user.Password);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    public async Task<HttpResponseMessage> SubmitLoginAsync(string email, string password, string? returnUrl = null)
    {
        var path = returnUrl is null ? "/Account/Login" : $"/Account/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
        var page = await Browser.GetAsync(path);
        var fields = HiddenFields(await page.Content.ReadAsStringAsync());
        fields["Input.Email"] = email;
        fields["Input.Password"] = password;
        return await Browser.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    /// <summary>Consent page fields, ready to post back.</summary>
    public async Task<Dictionary<string, string>> OpenConsentAsync(AuthorizationRequest request)
    {
        var response = await Browser.GetAsync(request.Query());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return HiddenFields(await response.Content.ReadAsStringAsync());
    }

    public async Task<HttpResponseMessage> DecideAsync(Dictionary<string, string> consentFields, bool approve, bool grantWrite)
    {
        var fields = new Dictionary<string, string>(consentFields)
        {
            [OAuthEndpoints.DecisionField] = approve ? OAuthEndpoints.ApproveValue : "deny",
        };
        if (grantWrite)
        {
            fields[OAuthEndpoints.GrantWriteField] = "on";
        }

        return await Browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(fields));
    }

    /// <summary>Runs consent and returns the authorization code delivered to the client's redirect URI.</summary>
    public async Task<string> ObtainCodeAsync(AuthorizationRequest request, bool grantWrite)
    {
        var fields = await OpenConsentAsync(request);
        var response = await DecideAsync(fields, approve: true, grantWrite);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        var location = response.Headers.Location!;
        Assert.StartsWith(request.RedirectUri, location.AbsoluteUri, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(request.State, query["state"].ToString());
        return query["code"].ToString();
    }

    public Task<HttpResponseMessage> RedeemAsync(AuthorizationRequest request, string code, string? verifier = null, string? resource = null)
    {
        return Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = request.ClientId,
            ["redirect_uri"] = request.RedirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier ?? request.CodeVerifier,
            ["resource"] = resource ?? app.McpEndpoint.AbsoluteUri,
        }));
    }

    public Task<HttpResponseMessage> RefreshAsync(string clientId, string refreshToken, string? scope = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["resource"] = app.McpEndpoint.AbsoluteUri,
        };
        if (scope is not null)
        {
            fields["scope"] = scope;
        }

        return Client.PostAsync("/connect/token", new FormUrlEncodedContent(fields));
    }

    /// <summary>Signs the user in on a fresh browser and completes the whole code flow.</summary>
    public static async Task<TokenSet> ConnectAsync(LabHopApp app, TestUser user, bool grantWrite, string scope = AllScopes)
    {
        var driver = new OAuthDriver(app);
        await driver.SignInAsync(user);
        var request = driver.NewRequest(scope: scope);
        var code = await driver.ObtainCodeAsync(request, grantWrite);
        return await ReadTokensAsync(await driver.RedeemAsync(request, code));
    }

    public static async Task<TokenSet> ReadTokensAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Token request failed: {(int)response.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new TokenSet(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            root.TryGetProperty("scope", out var scope) ? scope.GetString() : null);
    }

    public static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    public static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match input in InputTag().Matches(html))
        {
            if (input.Value.Contains("type=\"hidden\"", StringComparison.Ordinal))
            {
                var name = NameAttribute().Match(input.Value).Groups[1].Value;
                var value = ValueAttribute().Match(input.Value).Groups[1].Value;
                fields[WebUtility.HtmlDecode(name)] = WebUtility.HtmlDecode(value);
            }
        }

        return fields;
    }

    [GeneratedRegex("<input[^>]*>")]
    private static partial Regex InputTag();

    [GeneratedRegex("name=\"([^\"]*)\"")]
    private static partial Regex NameAttribute();

    [GeneratedRegex("value=\"([^\"]*)\"")]
    private static partial Regex ValueAttribute();
}
