using OpenIddict.Abstractions;

namespace LabHop.Background;

/// <summary>Removes expired or revoked OAuth tokens and authorizations so the tables do not grow forever.</summary>
public sealed class OAuthPruningService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OAuthPruningService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var threshold = clock.GetUtcNow() - Retention;
                await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(threshold, stoppingToken);
                await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(threshold, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Pruning OAuth tokens and authorizations failed; it will run again later.");
            }
        }
    }
}
