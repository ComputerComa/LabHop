using System.Net;

namespace LabHop.Options;

/// <summary>
/// Deployment settings. <see cref="PublicBaseUrl"/> is the single source for the OAuth issuer and the
/// canonical MCP resource URI, so neither is ever derived from request headers.
/// </summary>
public sealed class LabHopOptions
{
    public const string SectionName = "LabHop";
    public const string McpPath = "/mcp";

    public required Uri PublicBaseUrl { get; init; }

    /// <summary>Absolute directory holding OAuth certificates and Data Protection keys.</summary>
    public required string KeysPath { get; init; }

    /// <summary>Reverse proxy networks (CIDR) trusted for X-Forwarded-*, in addition to loopback.</summary>
    public IReadOnlyList<IPNetwork> KnownProxyNetworks { get; init; } = [];

    /// <summary>Browser origins, beyond the public origin, allowed to call the MCP endpoint.</summary>
    public IReadOnlyList<string> McpAllowedOrigins { get; init; } = [];

    /// <summary>The OAuth issuer. Always ends in "/", and must match byte for byte everywhere it is published.</summary>
    public string Issuer => PublicBaseUrl.AbsoluteUri;

    /// <summary>The canonical MCP server URI (RFC 8707 resource, token audience).</summary>
    public string McpResource => new Uri(PublicBaseUrl, McpPath).AbsoluteUri;

    public string ProtectedResourceMetadataUrl =>
        new Uri(PublicBaseUrl, $"/.well-known/oauth-protected-resource{McpPath}").AbsoluteUri;

    public string PublicOrigin => PublicBaseUrl.GetLeftPart(UriPartial.Authority);

    public static LabHopOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(SectionName);

        var rawBaseUrl = section["PublicBaseUrl"]
            ?? throw new InvalidOperationException("LabHop:PublicBaseUrl is required, for example https://labhop.example.net/.");
        if (!Uri.TryCreate(rawBaseUrl, UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme is not ("http" or "https")
            || baseUrl.AbsolutePath != "/"
            || baseUrl.Query.Length > 0
            || baseUrl.Fragment.Length > 0
            || baseUrl.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"LabHop:PublicBaseUrl '{rawBaseUrl}' must be an absolute http(s) URL with no path, query, or fragment.");
        }

        if (baseUrl.Scheme != "https" && !(environment.IsDevelopment() && baseUrl.IsLoopback))
        {
            throw new InvalidOperationException(
                "LabHop:PublicBaseUrl must use https. Plain http is accepted only for a loopback host in Development.");
        }

        var keysPath = section["KeysPath"]
            ?? throw new InvalidOperationException("LabHop:KeysPath is required, for example /var/lib/labhop/keys.");

        return new LabHopOptions
        {
            PublicBaseUrl = baseUrl,
            KeysPath = Path.GetFullPath(keysPath, environment.ContentRootPath),
            KnownProxyNetworks = (section.GetSection("KnownProxyNetworks").Get<string[]>() ?? [])
                .Select(IPNetwork.Parse)
                .ToList(),
            McpAllowedOrigins = section.GetSection("McpAllowedOrigins").Get<string[]>() ?? [],
        };
    }
}
