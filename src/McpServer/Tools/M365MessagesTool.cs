using System.ComponentModel;
using System.Net.Http.Headers;
using McpServer.Auth;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Office 365 Message Center ("Nachrichten") über die Microsoft Graph Service-Communications-API.
/// Benötigt Application Permission "ServiceMessage.Read.All" (Admin Consent).
/// </summary>
[McpServerToolType]
public class M365MessagesTool
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApiTokenService _tokenService;

    public M365MessagesTool(IHttpClientFactory httpClientFactory, ApiTokenService tokenService)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
    }

    [McpServerTool, Description("Liest die Message-Center-Beiträge (Ankündigungen zu neuen/geänderten Features) des Tenants.")]
    public async Task<string> GetMessageCenterMessages(
        [Description("Optionaler OData-Filter, z.B. \"category eq 'stayInformed'\". Leer lassen für alle Nachrichten.")]
        string? odataFilter = null)
    {
        var path = "/admin/serviceAnnouncement/messages";
        if (!string.IsNullOrWhiteSpace(odataFilter))
        {
            path += $"?$filter={Uri.EscapeDataString(odataFilter)}";
        }

        var token = await _tokenService.GetGraphAppOnlyTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
