using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenIddict.Server;
using Xunit;

namespace LabHop.Tests.Infrastructure;

/// <summary>One running application and database per test class. Tests stay independent by creating their own users.</summary>
public class LabHopAppFixture(PostgresServer postgres) : IAsyncLifetime
{
    public LabHopApp App { get; private set; } = null!;

    protected virtual Action<IServiceCollection>? ConfigureServices => null;

    public async ValueTask InitializeAsync() => App = await LabHopApp.StartAsync(postgres, ConfigureServices);

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await using var connection = new NpgsqlConnection(App.ConnectionString);
        NpgsqlConnection.ClearPool(connection);
    }
}

/// <summary>Zero leeway makes refresh token replay detectable immediately instead of after 30 seconds.</summary>
public sealed class ZeroReuseLeewayAppFixture(PostgresServer postgres) : LabHopAppFixture(postgres)
{
    protected override Action<IServiceCollection>? ConfigureServices => services =>
        services.Configure<OpenIddictServerOptions>(options => options.RefreshTokenReuseLeeway = TimeSpan.Zero);
}
