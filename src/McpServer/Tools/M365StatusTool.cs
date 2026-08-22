using System.ComponentModel;
using System.Net.Http.Headers;
using McpServer.Auth;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Office 365 Status (Service Health) über die Microsoft Graph Service-Communications-API.
/// Benötigt Application Permission "ServiceHealth.Read.All" (Admin Consent), siehe
/// scripts/entra-setup.sh bzw. terraform/modules/entra-id.
/// </summary>
[McpServerToolType]
public class M365StatusTool
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApiTokenService _tokenService;

    public M365StatusTool(IHttpClientFactory httpClientFactory, ApiTokenService tokenService)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
    }

    [McpServerTool, Description("Gibt den aktuellen Gesamt-Servicestatus (Health Overview) aller Microsoft-365-Dienste des Tenants zurück.")]
    public async Task<string> GetServiceHealthOverview()
    {
        return await CallGraphAsync("/admin/serviceAnnouncement/healthOverviews");
    }

    [McpServerTool, Description("Gibt aktuelle und kürzlich abgeschlossene Serviceincidents (Störungen) für den Tenant zurück.")]
    public async Task<string> GetServiceHealthIssues()
    {
        return await CallGraphAsync("/admin/serviceAnnouncement/issues");
    }

    private async Task<string> CallGraphAsync(string relativePath)
    {
        var token = await _tokenService.GetGraphAppOnlyTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync(relativePath);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
