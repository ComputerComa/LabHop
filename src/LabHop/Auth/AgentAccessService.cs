using LabHop.Models;
using LabHop.Services;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LabHop.Auth;

public sealed record AgentConnection(string AuthorizationId, string ClientName, IReadOnlyList<string> Scopes, DateTime? CreatedAtUtc);

/// <summary>
/// Lists and revokes the OAuth grants a user has approved. Each operation uses its own scope because
/// the OpenIddict managers hold a scoped DbContext, which must not live as long as a Blazor circuit.
/// </summary>
public sealed class AgentAccessService(IServiceScopeFactory scopes)
{
    public async Task<IReadOnlyList<AgentConnection>> ListAsync(Actor actor, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        var connections = new List<AgentConnection>();
        await foreach (var authorization in authorizations.FindBySubjectAsync(actor.UserId, cancellationToken))
        {
            if (!await authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken))
            {
                continue;
            }

            var applicationId = await authorizations.GetApplicationIdAsync(authorization, cancellationToken);
            var application = applicationId is null ? null : await applications.FindByIdAsync(applicationId, cancellationToken);
            var name = application is null ? null : await applications.GetDisplayNameAsync(application, cancellationToken);
            var created = await authorizations.GetCreationDateAsync(authorization, cancellationToken);

            connections.Add(new AgentConnection(
                await authorizations.GetIdAsync(authorization, cancellationToken) ?? "",
                name ?? "Unknown client",
                [.. await authorizations.GetScopesAsync(authorization, cancellationToken)],
                created?.UtcDateTime));
        }

        return connections.OrderByDescending(connection => connection.CreatedAtUtc).ToList();
    }

    /// <summary>Revokes the grant and its tokens. A grant belonging to another user is reported as missing.</summary>
    public async Task RevokeAsync(Actor actor, string authorizationId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        var authorization = await authorizations.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null || await authorizations.GetSubjectAsync(authorization, cancellationToken) != actor.UserId)
        {
            throw new RuleViolationException("That agent connection no longer exists.");
        }

        await authorizations.TryRevokeAsync(authorization, cancellationToken);
        await tokens.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken);
    }
}
