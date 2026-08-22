namespace McpServer.Auth;

public class McpAuthOptions
{
    public string ExternalBaseUrl { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool RequireAuthentication { get; set; } = true;
}
