using System.ComponentModel;
using McpServer.Services;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Office 365 Adoption / Usage Reports über die Microsoft Graph Reports-API.
/// Benötigt Application Permission "Reports.Read.All" (Admin Consent).
///
/// Liefert immer JSON zurück (siehe Services/GraphReportsClient.cs):
///   - versucht zuerst den Beta-Endpunkt mit "$format=application/json"
///   - fällt automatisch auf CSV-Download + Konvertierung zurück, falls Graph CSV liefert
/// </summary>
[McpServerToolType]
public class M365AdoptionTool
{
    private readonly GraphReportsClient _reportsClient;

    public M365AdoptionTool(GraphReportsClient reportsClient)
    {
        _reportsClient = reportsClient;
    }

    [McpServerTool, Description("Liefert den Office 365 Active User Detail Report (Adoption je Dienst: Exchange, SharePoint, Teams, ...) als JSON.")]
    public async Task<string> GetOffice365ActiveUserDetail(
        [Description("Zeitraum, z.B. D7, D30, D90, D180. Standard: D30.")]
        string period = "D30")
    {
        return await _reportsClient.GetReportAsJsonAsync($"/reports/getOffice365ActiveUserDetail(period='{period}')");
    }

    [McpServerTool, Description("Liefert den Microsoft-365-App-Nutzungsreport (Desktop/Mobile/Web je App) als JSON.")]
    public async Task<string> GetM365AppUserDetail(
        [Description("Zeitraum, z.B. D7, D30, D90, D180. Standard: D30.")]
        string period = "D30")
    {
        return await _reportsClient.GetReportAsJsonAsync($"/reports/getM365AppUserDetail(period='{period}')");
    }
}
