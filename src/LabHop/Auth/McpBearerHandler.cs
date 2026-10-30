using System.Text.Encodings.Web;
using LabHop.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore.Authentication;

namespace LabHop.Auth;

/// <summary>
/// Adds to the SDK handler, which serves protected resource metadata but emits only
/// <c>resource_metadata</c> on 401 and no challenge on 403. Token validation is forwarded to OpenIddict.
/// </summary>
public sealed class McpBearerHandler(
    IOptionsMonitor<McpAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LabHopOptions labHop) : McpAuthenticationHandler(options, logger, encoder)
{
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // A failed token gets an error code (RFC 6750); a request with no credentials gets none.
        var presentedToken = Request.Headers.Authorization.Any(value => value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true);
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, McpChallenges.Unauthorized(labHop, presentedToken));
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, McpChallenges.InsufficientScope(labHop));
        return Task.CompletedTask;
    }
}

public static class McpChallenges
{
    public static string Unauthorized(LabHopOptions options, bool presentedToken)
    {
        var error = presentedToken ? "error=\"invalid_token\", " : "";
        return $"Bearer {error}resource_metadata=\"{options.ProtectedResourceMetadataUrl}\", scope=\"{LabHopScopes.All}\"";
    }

    public static string InsufficientScope(LabHopOptions options) =>
        $"Bearer error=\"insufficient_scope\", scope=\"{LabHopScopes.All}\", resource_metadata=\"{options.ProtectedResourceMetadataUrl}\", " +
        "error_description=\"This operation needs more access. Connect again and grant the requested access on the LabHop consent page.\"";
}
