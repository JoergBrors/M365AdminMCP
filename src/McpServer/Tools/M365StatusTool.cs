using System.ComponentModel;
using McpServer.Services;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Office 365 Status (Service Health) ueber den zentralen ApiServer.
/// </summary>
[McpServerToolType]
public class M365StatusTool
{
    private readonly ApiServerClient _apiServerClient;

    public M365StatusTool(ApiServerClient apiServerClient)
    {
        _apiServerClient = apiServerClient;
    }

    [McpServerTool, Description("Gibt den aktuellen Gesamt-Servicestatus (Health Overview) aller Microsoft-365-Dienste des Tenants zurück.")]
    public async Task<string> GetServiceHealthOverview()
    {
        return await _apiServerClient.GetAsync("/api/m365/status/health-overviews");
    }

    [McpServerTool, Description("Gibt Details zu einem Microsoft-365-Service aus der Health Overview zurück, optional inklusive zugehöriger Issues.")]
    public async Task<string> GetServiceHealthDetail(
        [Description("Service-ID aus GetServiceHealthOverview, z.B. Exchange, SharePoint, OrgLiveID oder OSDPPlatform.")]
        string serviceHealthId,
        [Description("Wenn true, liefert Microsoft Graph die zugehörigen Issues direkt über $expand=issues mit.")]
        bool expandIssues = true,
        [Description("Wenn false, werden bei Fallback nur aktuell offene/nicht gelöste Issues für den Service zurückgegeben.")]
        bool includeResolvedIssues = false)
    {
        return await _apiServerClient.GetAsync(
            $"/api/m365/status/health-overviews/{Uri.EscapeDataString(serviceHealthId)}" +
            $"?expandIssues={expandIssues.ToString().ToLowerInvariant()}" +
            $"&includeResolvedIssues={includeResolvedIssues.ToString().ToLowerInvariant()}");
    }

    [McpServerTool, Description("Gibt nur die aktuell nicht ServiceOperational gemeldeten Dienste mit Detaildaten und Issues zurück.")]
    public async Task<string> GetDegradedServiceHealthDetails()
    {
        return await _apiServerClient.GetAsync("/api/m365/status/health-overviews/degraded");
    }

    [McpServerTool, Description("Gibt aktuelle und kürzlich abgeschlossene Serviceincidents (Störungen) für den Tenant zurück.")]
    public async Task<string> GetServiceHealthIssues()
    {
        return await _apiServerClient.GetAsync("/api/m365/status/issues");
    }
}
