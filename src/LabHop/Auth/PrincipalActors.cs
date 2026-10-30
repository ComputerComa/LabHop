using System.Security.Claims;
using LabHop.Models;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LabHop.Auth;

/// <summary>Builds an <see cref="Actor"/> from an authenticated principal. Identity never comes from user input.</summary>
public static class PrincipalActors
{
    /// <summary>The OAuth client's display name, stored in the access token at consent time.</summary>
    public const string ClientNameClaim = "labhop_client_name";

    public static Actor ForBrowser(ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The browser session has no user identifier.");
        return new Actor(userId, ActorKind.Human, user.Identity?.Name ?? userId);
    }

    public static Actor ForAgent(ClaimsPrincipal? user)
    {
        var userId = user?.GetClaim(Claims.Subject)
            ?? throw new InvalidOperationException("The access token has no subject.");
        var clientId = user.GetClaim(Claims.ClientId);
        return new Actor(userId, ActorKind.Agent, user.GetClaim(ClientNameClaim) ?? clientId ?? "Unknown agent", clientId);
    }
}
