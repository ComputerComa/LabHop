using LabHop.Options;
using LabHop.Services;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LabHop.Auth;

public sealed record OAuthClientInfo(string ClientId, string DisplayName, IReadOnlyList<string> RedirectUris);

/// <summary>
/// Pre-registers public OAuth clients. A client is either loopback (a local CLI agent, matched on any
/// port) or uses exact https redirect URIs (a hosted agent). See docs/oauth-clients.md.
/// </summary>
public sealed class OAuthClientRegistry(IOpenIddictApplicationManager applications, LabHopOptions options)
{
    public static readonly IReadOnlyList<string> StandardLoopbackRedirects =
        ["http://localhost/callback", "http://127.0.0.1/callback", "http://[::1]/callback"];

    public async Task<OAuthClientInfo> AddAsync(string clientId, string displayName, IReadOnlyCollection<string> redirectUris, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            errors["clientId"] = "A client ID is required.";
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            errors["name"] = "A display name is required.";
        }

        if (redirectUris.Count == 0)
        {
            errors["redirectUri"] = "At least one redirect URI is required.";
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        if (await applications.FindByClientIdAsync(clientId, cancellationToken) is not null)
        {
            throw new RuleViolationException($"OAuth client '{clientId}' already exists.");
        }

        var (normalized, isLoopback) = NormalizeRedirects(redirectUris);

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = displayName,
            ClientType = ClientTypes.Public,
            ApplicationType = isLoopback ? ApplicationTypes.Native : ApplicationTypes.Web,
            ConsentType = ConsentTypes.Explicit,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + LabHopScopes.Read,
                Permissions.Prefixes.Scope + LabHopScopes.Write,
                Permissions.Prefixes.Resource + options.McpResource,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var uri in normalized)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        await applications.CreateAsync(descriptor, cancellationToken);
        return new OAuthClientInfo(clientId, displayName, normalized);
    }

    public async Task<IReadOnlyList<OAuthClientInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var clients = new List<OAuthClientInfo>();
        await foreach (var application in applications.ListAsync(count: null, offset: null, cancellationToken))
        {
            clients.Add(new OAuthClientInfo(
                await applications.GetClientIdAsync(application, cancellationToken) ?? "",
                await applications.GetDisplayNameAsync(application, cancellationToken) ?? "",
                [.. await applications.GetRedirectUrisAsync(application, cancellationToken)]));
        }

        return clients;
    }

    /// <summary>Removes the client and, with it, every authorization and token issued to it.</summary>
    public async Task<bool> RemoveAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var application = await applications.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
        {
            return false;
        }

        await applications.DeleteAsync(application, cancellationToken);
        return true;
    }

    /// <summary>
    /// Loopback URIs lose their port so OpenIddict's native-client rule (RFC 8252 section 7.3) matches any
    /// port. Everything else must be https and is matched exactly. One client cannot mix the two, since
    /// the application type that enables the loopback rule applies to the whole client.
    /// </summary>
    private static (List<string> Redirects, bool IsLoopback) NormalizeRedirects(IEnumerable<string> redirectUris)
    {
        var normalized = new List<string>();
        var loopbackCount = 0;
        foreach (var raw in redirectUris)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            {
                throw Invalid($"Redirect URI '{raw}' must be an absolute URI with no fragment or user info.");
            }

            if (uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)
            {
                loopbackCount++;
                normalized.Add($"http://{uri.Host}{uri.PathAndQuery}");
            }
            else if (uri.Scheme == Uri.UriSchemeHttps && !uri.IsLoopback)
            {
                normalized.Add(uri.AbsoluteUri);
            }
            else
            {
                throw Invalid($"Redirect URI '{raw}' must be https, or http on a loopback host.");
            }
        }

        if (loopbackCount > 0 && loopbackCount < normalized.Count)
        {
            throw Invalid("A client cannot mix loopback and https redirect URIs. Register two clients instead.");
        }

        return (normalized.Distinct().ToList(), loopbackCount > 0);

        static ValidationFailedException Invalid(string message) =>
            new(new Dictionary<string, string> { ["redirectUri"] = message });
    }
}
