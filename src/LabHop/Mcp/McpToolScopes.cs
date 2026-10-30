using System.Reflection;
using System.Text.Json;
using LabHop.Auth;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace LabHop.Mcp;

/// <summary>
/// Maps tool names to their required scope by reading each tool's <see cref="AuthorizeAttribute"/>, so
/// there is one declaration per tool. A tool without a recognized policy fails startup.
/// </summary>
public static class McpToolScopes
{
    private static readonly Dictionary<string, string> Required = Build();

    public static string? ForTool(string toolName) => Required.GetValueOrDefault(toolName);

    /// <summary>
    /// The scope a JSON-RPC payload needs, or null when it needs nothing beyond endpoint access.
    /// Unparseable input also returns null and is left for the SDK to reject.
    /// </summary>
    public static async Task<string?> RequiredByPayloadAsync(Stream body, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var messages = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : [document.RootElement];

            var scopes = messages.Select(ScopeForMessage).Where(scope => scope is not null).ToList();
            return scopes.Contains(LabHopScopes.Write) ? LabHopScopes.Write : scopes.FirstOrDefault();
        }
    }

    private static string? ScopeForMessage(JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String
            && method.ValueEquals("tools/call")
            && message.TryGetProperty("params", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String)
        {
            return ForTool(name.GetString()!);
        }

        return null;
    }

    private static Dictionary<string, string> Build()
    {
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        var methods = typeof(McpToolScopes).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);

        foreach (var method in methods)
        {
            var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? method.Name;
            scopes[name] = method.GetCustomAttribute<AuthorizeAttribute>()?.Policy switch
            {
                McpPolicies.Read => LabHopScopes.Read,
                McpPolicies.Write => LabHopScopes.Write,
                var policy => throw new InvalidOperationException(
                    $"MCP tool '{name}' must declare [Authorize(Policy = ...)] with a scope policy, but has '{policy ?? "none"}'."),
            };
        }

        return scopes;
    }
}
