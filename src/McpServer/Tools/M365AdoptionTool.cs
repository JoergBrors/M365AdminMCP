using System.ComponentModel;
using McpServer.Services;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Office 365 Adoption / Usage Reports ueber den zentralen ApiServer.
/// </summary>
[McpServerToolType]
public class M365AdoptionTool
{
    private readonly ApiServerClient _apiServerClient;

    public M365AdoptionTool(ApiServerClient apiServerClient)
    {
        _apiServerClient = apiServerClient;
    }

    [McpServerTool, Description("Liefert den Office 365 Active User Detail Report (Adoption je Dienst: Exchange, SharePoint, Teams, ...) als JSON.")]
    public async Task<string> GetOffice365ActiveUserDetail(
        [Description("Zeitraum, z.B. D7, D30, D90, D180. Standard: D30.")]
        string period = "D30")
    {
        return await _apiServerClient.GetAsync($"/api/m365/reports/getOffice365ActiveUserDetail?period={Uri.EscapeDataString(period)}");
    }

    [McpServerTool, Description("Liefert den Microsoft-365-App-Nutzungsreport (Desktop/Mobile/Web je App) als JSON.")]
    public async Task<string> GetM365AppUserDetail(
        [Description("Zeitraum, z.B. D7, D30, D90, D180. Standard: D30.")]
        string period = "D30")
    {
        return await _apiServerClient.GetAsync($"/api/m365/reports/getM365AppUserDetail?period={Uri.EscapeDataString(period)}");
    }
}
