using System.ComponentModel;
using McpServer.Services;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Office 365 Message Center ("Nachrichten") ueber den zentralen ApiServer.
/// </summary>
[McpServerToolType]
public class M365MessagesTool
{
    private readonly ApiServerClient _apiServerClient;

    public M365MessagesTool(ApiServerClient apiServerClient)
    {
        _apiServerClient = apiServerClient;
    }

    [McpServerTool, Description("Liest die Message-Center-Beiträge (Ankündigungen zu neuen/geänderten Features) des Tenants.")]
    public async Task<string> GetMessageCenterMessages(
        [Description("Optionaler OData-Filter, z.B. \"category eq 'stayInformed'\". Leer lassen für alle Nachrichten.")]
        string? odataFilter = null)
    {
        var path = "/api/m365/messages";
        if (!string.IsNullOrWhiteSpace(odataFilter))
        {
            path += $"?odataFilter={Uri.EscapeDataString(odataFilter)}";
        }

        return await _apiServerClient.GetAsync(path);
    }
}
