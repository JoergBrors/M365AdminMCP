using System.ComponentModel;
using McpServer.Auth;
using McpServer.Services;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

// HINWEIS: Das MCP-C#-SDK ist Stand jetzt Preview. Die Attribute [McpServerToolType]/[McpServerTool]
// entsprechen dem aktuell dokumentierten Muster von ModelContextProtocol.AspNetCore; bitte gegen die
// zum Build-Zeitpunkt aktuelle Paketversion/Doku prüfen, falls sich die API geändert hat.

[McpServerToolType]
public class TasksTool
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApiTokenService _tokenService;
    private readonly ApiServerClient _apiServerClient;

    public TasksTool(IHttpClientFactory httpClientFactory, ApiTokenService tokenService, ApiServerClient apiServerClient)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
        _apiServerClient = apiServerClient;
    }

    [McpServerTool, Description("Liest die Task-Liste vom API Server (App-only Zugriff).")]
    public async Task<string> GetTasksAppOnly()
    {
        return await _apiServerClient.GetAsync("/api/tasks");
    }

    [McpServerTool, Description("Liest die Task-Liste vom API Server im Namen des aktuell angemeldeten Nutzers (delegated, On-Behalf-Of).")]
    public async Task<string> GetTasksDelegated(
        [Description("Das eingehende Access Token des Nutzers, das gegen den API Server per OBO getauscht wird.")]
        string incomingUserAccessToken)
    {
        var token = await _tokenService.GetOnBehalfOfTokenAsync(incomingUserAccessToken);
        return await _apiServerClient.GetAsync("/api/tasks", token);
    }
}
