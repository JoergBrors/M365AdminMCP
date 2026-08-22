using System.ComponentModel;
using System.Net.Http.Headers;
using McpServer.Auth;
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
    private readonly IConfiguration _configuration;

    public TasksTool(IHttpClientFactory httpClientFactory, ApiTokenService tokenService, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
        _configuration = configuration;
    }

    [McpServerTool, Description("Liest die Task-Liste vom API Server (App-only Zugriff).")]
    public async Task<string> GetTasksAppOnly()
    {
        var token = await _tokenService.GetAppOnlyTokenAsync();
        return await CallApiAsync(token);
    }

    [McpServerTool, Description("Liest die Task-Liste vom API Server im Namen des aktuell angemeldeten Nutzers (delegated, On-Behalf-Of).")]
    public async Task<string> GetTasksDelegated(
        [Description("Das eingehende Access Token des Nutzers, das gegen den API Server per OBO getauscht wird.")]
        string incomingUserAccessToken)
    {
        var token = await _tokenService.GetOnBehalfOfTokenAsync(incomingUserAccessToken);
        return await CallApiAsync(token);
    }

    private async Task<string> CallApiAsync(string bearerToken)
    {
        var apiBaseUrl = _configuration["ApiServer:BaseUrl"]
            ?? throw new InvalidOperationException("ApiServer:BaseUrl ist nicht konfiguriert.");

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(apiBaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var response = await client.GetAsync("/api/tasks");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
