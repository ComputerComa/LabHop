using LabHop.Auth;
using LabHop.Data;
using LabHop.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabHop.Tests.Infrastructure;

public sealed record TestUser(string Id, string Email, string Password)
{
    public Actor AsHuman => new(Id, ActorKind.Human, Email);
    public Actor AsAgent(string clientId = "test-agent") => new(Id, ActorKind.Agent, "Test Agent", clientId);
}

/// <summary>The real application over a real database, in Production mode, with HTTPS at the test server.</summary>
public sealed class LabHopApp : WebApplicationFactory<Program>
{
    public const string LoopbackClientId = "cli-agent";
    public const string LoopbackClientName = "CLI Agent";
    public const string HostedClientId = "hosted-agent";
    public const string HostedRedirect = "https://agent.example.net/oauth/callback";

    private readonly Uri publicBaseUrl;

    public LabHopApp(string connectionString, string keysPath, string publicBaseUrl = "https://localhost/")
    {
        ConnectionString = connectionString;
        KeysPath = keysPath;
        this.publicBaseUrl = new Uri(publicBaseUrl);
    }

    public string ConnectionString { get; }
    public string KeysPath { get; }
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Extra service changes applied after the application registers its own.</summary>
    public Action<IServiceCollection>? ConfigureTestServices { get; init; }

    public Uri BaseAddress => publicBaseUrl;
    public Uri McpEndpoint => new(publicBaseUrl, "/mcp");

    public static async Task<LabHopApp> StartAsync(PostgresServer postgres, Action<IServiceCollection>? configureTestServices = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await MigrateAsync(connectionString);
        var app = new LabHopApp(connectionString, CreateKeysPath()) { ConfigureTestServices = configureTestServices };
        _ = app.Server;
        await app.RegisterClientsAsync();
        return app;
    }

    public static string CreateKeysPath() =>
        Directory.CreateTempSubdirectory("labhop-test-keys-").FullName;

    public static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).UseOpenIddict().Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
        builder.UseSetting("LabHop:PublicBaseUrl", publicBaseUrl.AbsoluteUri);
        builder.UseSetting("LabHop:KeysPath", KeysPath);
        builder.UseSetting("Logging:LogLevel:Default", "Information");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Information");
        builder.UseSetting("Logging:LogLevel:OpenIddict", "Information");
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services => ConfigureTestServices?.Invoke(services));
    }

    public async Task RegisterClientsAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var clients = scope.ServiceProvider.GetRequiredService<OAuthClientRegistry>();
        await clients.AddAsync(LoopbackClientId, LoopbackClientName, OAuthClientRegistry.StandardLoopbackRedirects.ToList());
        await clients.AddAsync(HostedClientId, "Hosted Agent", [HostedRedirect]);
    }

    public async Task<TestUser> CreateUserAsync()
    {
        var email = $"user-{Guid.NewGuid():N}@example.net";
        const string password = "Correct-horse-battery-staple-7";

        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(' ', result.Errors.Select(error => error.Description)));
        }

        return new TestUser(user.Id, email, password);
    }

    /// <summary>A client that behaves like a browser: it keeps cookies and does not follow redirects.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = publicBaseUrl,
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>A cookie-less protocol client.</summary>
    public HttpClient CreateProtocolClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = publicBaseUrl,
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    public ApplicationDbContext CreateDb() =>
        Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
}
