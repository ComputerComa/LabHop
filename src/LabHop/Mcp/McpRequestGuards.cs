using LabHop.Auth;
using LabHop.Options;
using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;

namespace LabHop.Mcp;

/// <summary>HTTP-level checks in front of the MCP SDK, which runs after the response has already started.</summary>
public static class McpRequestGuards
{
    /// <summary>Matches by segment because routing also sends "/mcp/" to the MCP endpoint.</summary>
    private static bool IsMcpRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments(LabHopOptions.McpPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Streamable HTTP transport requires servers to validate Origin; the SDK does not.</summary>
    public static IApplicationBuilder UseMcpOriginValidation(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<LabHopOptions>();
        var allowed = options.McpAllowedOrigins.Append(options.PublicOrigin).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return app.Use(async (context, next) =>
        {
            if (IsMcpRequest(context)
                && context.Request.Headers.TryGetValue(HeaderNames.Origin, out var origin)
                && !allowed.Contains(origin.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "origin_not_allowed", error_description = "This Origin may not call the MCP endpoint." });
                return;
            }

            await next();
        });
    }

    /// <summary>
    /// Turns a tool call lacking the needed scope into the HTTP 403 that OAuth clients act on. Tool-level
    /// [Authorize] remains the authoritative check, so a payload this parser reads differently from the
    /// SDK still cannot reach a tool without the scope.
    /// </summary>
    public static IApplicationBuilder UseMcpScopeChallenge(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<LabHopOptions>();

        return app.Use(async (context, next) =>
        {
            if (IsMcpRequest(context) && HttpMethods.IsPost(context.Request.Method) && context.User.Identity?.IsAuthenticated == true)
            {
                context.Request.EnableBuffering();
                var required = await McpToolScopes.RequiredByPayloadAsync(context.Request.Body, context.RequestAborted);
                context.Request.Body.Position = 0;

                if (required is not null && !context.User.HasScope(required))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.Headers.Append(HeaderNames.WWWAuthenticate, McpChallenges.InsufficientScope(options));
                    return;
                }
            }

            await next();
        });
    }
}
