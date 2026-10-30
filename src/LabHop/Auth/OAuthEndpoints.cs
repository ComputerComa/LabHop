using System.Security.Claims;
using LabHop.Components.Shared;
using LabHop.Data;
using LabHop.Options;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LabHop.Auth;

/// <summary>
/// The application side of the OpenIddict passthrough endpoints. OpenIddict has already validated the
/// client, redirect URI, PKCE, scopes, and resource before these run; they decide who is signing in and
/// what that user consents to.
/// </summary>
public static class OAuthEndpoints
{
    public const string DecisionField = "consent_decision";
    public const string GrantWriteField = "consent_grant_write";
    public const string ApproveValue = "approve";

    public static void MapOAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/connect/authorize", ShowConsentAsync).AllowAnonymous();
        endpoints.MapPost("/connect/authorize", DecideAsync).AllowAnonymous();
        endpoints.MapPost("/connect/token", ExchangeAsync).AllowAnonymous();

        // Unknown protocol paths are 404s, not redirects to the login page.
        endpoints.MapFallback("/connect/{**rest}", () => Results.NotFound()).AllowAnonymous();
        endpoints.MapFallback("/.well-known/{**rest}", () => Results.NotFound()).AllowAnonymous();
    }

    private static async Task<IResult> ShowConsentAsync(
        HttpContext context,
        UserManager<ApplicationUser> users,
        IOpenIddictApplicationManager applications,
        IAntiforgery antiforgery)
    {
        var request = GetRequest(context);

        var user = await GetSignedInUserAsync(context, users);
        if (user is null)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return RejectAuthorization(Errors.LoginRequired, "Sign-in is required.");
            }

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = context.Request.PathBase + context.Request.Path + context.Request.QueryString },
                [IdentityConstants.ApplicationScheme]);
        }

        // Public clients cannot prove their identity, so a previous approval is never reused silently.
        if (request.HasPromptValue(PromptValues.None))
        {
            return RejectAuthorization(Errors.ConsentRequired, "Interactive consent is required.");
        }

        var application = await FindApplicationAsync(applications, request);
        var redirectUri = new Uri(request.RedirectUri!);
        var requested = request.GetScopes();

        context.Response.Headers.CacheControl = "no-store";
        return new RazorComponentResult<OAuthConsent>(new Dictionary<string, object?>
        {
            [nameof(OAuthConsent.ClientName)] = await applications.GetDisplayNameAsync(application) ?? request.ClientId,
            [nameof(OAuthConsent.UserName)] = user.UserName,
            [nameof(OAuthConsent.RedirectHost)] = redirectUri.Host,
            [nameof(OAuthConsent.IsLoopbackRedirect)] = redirectUri.IsLoopback,
            [nameof(OAuthConsent.WriteRequested)] = requested.Contains(LabHopScopes.Write),
            [nameof(OAuthConsent.OfflineRequested)] = requested.Contains(Scopes.OfflineAccess),
            [nameof(OAuthConsent.AntiforgeryFieldName)] = antiforgery.GetAndStoreTokens(context).FormFieldName,
            [nameof(OAuthConsent.AntiforgeryToken)] = antiforgery.GetAndStoreTokens(context).RequestToken,
            [nameof(OAuthConsent.RequestParameters)] = context.Request.Query
                .SelectMany(pair => pair.Value.Select(value => KeyValuePair.Create(pair.Key, value ?? "")))
                .ToList(),
        });
    }

    private static async Task<IResult> DecideAsync(
        HttpContext context,
        UserManager<ApplicationUser> users,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        var request = GetRequest(context);
        var user = await GetSignedInUserAsync(context, users);
        if (user is null)
        {
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                [IdentityConstants.ApplicationScheme]);
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!StringValues.Equals(form[DecisionField], ApproveValue))
        {
            return RejectAuthorization(Errors.AccessDenied, "The user denied this request.");
        }

        var application = await FindApplicationAsync(applications, request);
        var clientName = await applications.GetDisplayNameAsync(application);
        var granted = GrantedScopes(request, grantWrite: StringValues.Equals(form[GrantWriteField], "on"));

        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name,
            Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id);
        identity.SetClaim(Claims.Name, user.UserName);
        if (clientName is not null)
        {
            identity.SetClaim(PrincipalActors.ClientNameClaim, clientName);
        }

        identity.SetScopes(granted);
        identity.SetResources(context.RequestServices.GetRequiredService<LabHopOptions>().McpResource);

        var applicationId = await applications.GetIdAsync(application);
        var authorization = await authorizations.CreateAsync(
            identity,
            user.Id,
            applicationId!,
            AuthorizationTypes.Permanent,
            identity.GetScopes());
        identity.SetAuthorizationId(await authorizations.GetIdAsync(authorization));
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext context, UserManager<ApplicationUser> users)
    {
        var request = GetRequest(context);
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            throw new InvalidOperationException("OpenIddict accepted a grant type this application does not enable.");
        }

        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var subject = result.Principal?.GetClaim(Claims.Subject);
        var user = subject is null ? null : await users.FindByIdAsync(subject);
        if (user is null || await users.IsLockedOutAsync(user))
        {
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "This sign-in is no longer valid.",
                }),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var identity = new ClaimsIdentity(
            result.Principal!.Claims,
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name,
            Claims.Role);
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Read is the baseline. Write needs both the client's request and the user's explicit approval. A
    /// request naming no LabHop scope gets read access rather than a token that can do nothing.
    /// </summary>
    private static List<string> GrantedScopes(OpenIddictRequest request, bool grantWrite)
    {
        var requested = request.GetScopes();
        var granted = new List<string> { LabHopScopes.Read };
        if (grantWrite && requested.Contains(LabHopScopes.Write))
        {
            granted.Add(LabHopScopes.Write);
        }

        if (requested.Contains(Scopes.OfflineAccess))
        {
            granted.Add(Scopes.OfflineAccess);
        }

        return granted;
    }

    private static async Task<ApplicationUser?> GetSignedInUserAsync(HttpContext context, UserManager<ApplicationUser> users)
    {
        var result = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded)
        {
            return null;
        }

        var user = await users.GetUserAsync(result.Principal);
        return user is null || await users.IsLockedOutAsync(user) ? null : user;
    }

    private static OpenIddictRequest GetRequest(HttpContext context) =>
        context.GetOpenIddictServerRequest()
        ?? throw new InvalidOperationException("OpenIddict did not process this request.");

    private static async Task<object> FindApplicationAsync(IOpenIddictApplicationManager applications, OpenIddictRequest request) =>
        await applications.FindByClientIdAsync(request.ClientId!)
        ?? throw new InvalidOperationException("OpenIddict validated a client that no longer exists.");

    private static IResult RejectAuthorization(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
