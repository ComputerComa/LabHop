using LabHop.Components.Account;
using LabHop.Data;
using LabHop.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using ModelContextProtocol.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LabHop.Auth;

public static class AuthRegistration
{
    public static IServiceCollection AddLabHopIdentity(this IServiceCollection services, LabHopOptions options, IHostEnvironment environment)
    {
        services.AddCascadingAuthenticationState();
        services.AddScoped<IdentityRedirectManager>();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        services.AddAuthentication(authentication =>
            {
                authentication.DefaultScheme = IdentityConstants.ApplicationScheme;
                authentication.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        services.AddIdentityCore<ApplicationUser>(identity =>
            {
                identity.SignIn.RequireConfirmedAccount = false;
                identity.User.RequireUniqueEmail = true;
                identity.Password.RequiredLength = 12;
                identity.Password.RequireNonAlphanumeric = false;
                identity.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager();

        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.LoginPath = "/Account/Login";
            cookie.AccessDeniedPath = "/Account/AccessDenied";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });

        var dataProtectionPath = Path.Combine(options.KeysPath, "data-protection");
        OAuthKeyStore.EnsureDirectory(dataProtectionPath);
        services.AddDataProtection()
            .SetApplicationName("LabHop")
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

        return services;
    }

    public static IServiceCollection AddLabHopOAuth(this IServiceCollection services, LabHopOptions options, IHostEnvironment environment)
    {
        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<ApplicationDbContext>())
            .AddServer(server =>
            {
                server.SetIssuer(new Uri(options.Issuer));
                server.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetRevocationEndpointUris("connect/revoke");

                server.AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange()
                    .AllowRefreshTokenFlow();

                server.RegisterScopes(LabHopScopes.Read, LabHopScopes.Write, Scopes.OfflineAccess);
                server.RegisterResources(options.McpResource);

                // Reference tokens make revocation immediate: every request is checked against the token's
                // database entry, and the token itself reveals nothing to the client.
                server.UseReferenceAccessTokens();
                server.SetAccessTokenLifetime(TimeSpan.FromHours(1));
                server.SetRefreshTokenLifetime(TimeSpan.FromDays(30));

                foreach (var certificate in OAuthKeyStore.LoadSigning(options.KeysPath))
                {
                    server.AddSigningCertificate(certificate);
                }

                foreach (var certificate in OAuthKeyStore.LoadEncryption(options.KeysPath))
                {
                    server.AddEncryptionCertificate(certificate);
                }

                var aspNetCore = server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();
                if (environment.IsDevelopment() && options.PublicBaseUrl.IsLoopback)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }

                // Advertise only what is implemented, because clients act on this metadata. This runs after
                // UseAspNetCore, which adds client_secret_basic to the defaults.
                server.Configure(settings =>
                {
                    settings.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);

                    // Every client is public, and no ID tokens are issued.
                    settings.ClientAuthenticationMethods.Clear();
                    settings.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.None);
                    settings.Scopes.Remove(Scopes.OpenId);

                    // Consent is always shown, and forcing a fresh login is not implemented.
                    settings.PromptValues.Remove(PromptValues.Login);
                    settings.PromptValues.Remove(PromptValues.SelectAccount);
                });
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();

                // The MCP specification allows access tokens only in the Authorization header.
                validation.UseAspNetCore()
                    .DisableAccessTokenExtractionFromQueryString()
                    .DisableAccessTokenExtractionFromBodyForm();
                validation.AddAudiences(options.McpResource);
                validation.EnableAuthorizationEntryValidation();
            });

        new AuthenticationBuilder(services).AddScheme<McpAuthenticationOptions, McpBearerHandler>(
            McpAuthenticationDefaults.AuthenticationScheme,
            McpAuthenticationDefaults.DisplayName,
            mcp =>
            {
                mcp.ForwardAuthenticate = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
                mcp.ResourceMetadata = new()
                {
                    Resource = options.McpResource,
                    AuthorizationServers = { options.Issuer },
                    ScopesSupported = [LabHopScopes.Read, LabHopScopes.Write],
                    ResourceName = "LabHop",
                };
            });

        services.AddAuthorizationBuilder()
            // Anything without an explicit rule requires a browser login.
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(McpPolicies.Access, policy => policy
                .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(LabHopScopes.Read) || context.User.HasScope(LabHopScopes.Write)))
            .AddPolicy(McpPolicies.Read, policy => policy
                .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(LabHopScopes.Read)))
            .AddPolicy(McpPolicies.Write, policy => policy
                .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(LabHopScopes.Write)));

        services.AddScoped<OAuthClientRegistry>();
        services.AddSingleton<AgentAccessService>();

        return services;
    }
}
