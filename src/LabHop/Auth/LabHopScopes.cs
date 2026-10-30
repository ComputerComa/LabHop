namespace LabHop.Auth;

public static class LabHopScopes
{
    public const string Read = "labhop.read";
    public const string Write = "labhop.write";

    /// <summary>Space-separated form used in WWW-Authenticate challenges.</summary>
    public const string All = $"{Read} {Write}";
}

public static class McpPolicies
{
    /// <summary>Required for any MCP request: an OAuth token carrying at least one LabHop scope.</summary>
    public const string Access = "mcp.access";
    public const string Read = "mcp.read";
    public const string Write = "mcp.write";
}
