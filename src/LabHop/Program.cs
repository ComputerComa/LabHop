using LabHop.Auth;
using LabHop.Background;
using LabHop.Cli;
using LabHop.Components;
using LabHop.Components.Account;
using LabHop.Components.Shared;
using LabHop.Data;
using LabHop.Mcp;
using LabHop.Options;
using LabHop.Services.Projects;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Radzen;

var isCli = LabHopCli.IsCommand(args);
var builder = WebApplication.CreateBuilder(isCli ? [] : args);

// OpenIddict's Information logs include each token request, and its redaction does not cover code_verifier.
builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);

var options = LabHopOptions.Load(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
builder.Services.AddDbContextFactory<ApplicationDbContext>(db => db.UseNpgsql(connectionString).UseOpenIddict());
builder.Services.AddScoped(services => services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.AddScoped<BrowserTimeZone>();

builder.Services.AddLabHopIdentity(options, builder.Environment);
builder.Services.AddLabHopOAuth(options, builder.Environment);
builder.Services.AddLabHopMcp();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddHostedService<OAuthPruningService>();

builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    // Host is deliberately not forwarded: the proxy must pass Host through, and host filtering checks it.
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var network in options.KnownProxyNetworks)
    {
        forwarded.KnownIPNetworks.Add(network);
    }
});

if (string.IsNullOrEmpty(builder.Configuration["AllowedHosts"]))
{
    builder.Services.Configure<HostFilteringOptions>(hosts => hosts.AllowedHosts = [options.PublicBaseUrl.Host]);
}

var app = builder.Build();

if (isCli)
{
    return await LabHopCli.RunAsync(args, app.Services);
}

try
{
    await DatabaseStartup.EnsureNoPendingMigrationsAsync(app.Services);
}
catch (DatabaseNotReadyException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 1;
}

app.UseForwardedHeaders();

// Protocol endpoints answer with protocol responses. Browser error pages are only for browser paths.
app.UseWhen(context => !IsProtocolPath(context.Request.Path), browser =>
{
    if (!app.Environment.IsDevelopment())
    {
        browser.UseExceptionHandler("/Error", createScopeForErrors: true);
    }
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use((context, next) =>
{
    if (IsProtocolPath(context.Request.Path) && context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
    {
        statusPages.Enabled = false;
    }

    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
    return next();
});

app.UseRouting();
app.UseMcpOriginValidation();
app.UseAuthentication();
app.UseAuthorization();
app.UseMcpScopeChallenge();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapAccountEndpoints();
app.MapOAuthEndpoints();
app.MapLabHopMcp();

await app.RunAsync();
return 0;

static bool IsProtocolPath(PathString path) =>
    path.StartsWithSegments(LabHopOptions.McpPath, StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/connect", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase);

public partial class Program;
