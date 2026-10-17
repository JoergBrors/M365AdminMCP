using System.ComponentModel;
using McpServer.Services;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

[McpServerToolType]
public class TasksTool
{
    private readonly ApiServerClient _apiServerClient;

    public TasksTool(ApiServerClient apiServerClient)
    {
        _apiServerClient = apiServerClient;
    }

    [McpServerTool, Description("Liest die Task-Liste vom API Server (App-only Zugriff).")]
    public async Task<string> GetTasksAppOnly()
    {
        return await _apiServerClient.GetAsync("/api/tasks");
    }
}
