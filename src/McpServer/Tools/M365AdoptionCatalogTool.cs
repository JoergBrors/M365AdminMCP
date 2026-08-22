using System.ComponentModel;
using McpServer.Services;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Voller Zugriff auf ALLE Microsoft Graph BETA Adoption-/Usage-Report-Endpunkte ueber den zentralen ApiServer.
/// </summary>
[McpServerToolType]
public class M365AdoptionCatalogTool
{
    private readonly ApiServerClient _apiServerClient;

    public M365AdoptionCatalogTool(ApiServerClient apiServerClient)
    {
        _apiServerClient = apiServerClient;
    }

    [McpServerTool, Description("Listet alle verfügbaren Microsoft-365-Adoption-/Usage-Report-Endpunkte (Microsoft Graph Beta) mit Kategorie, Beschreibung und benötigten Parametern auf.")]
    public async Task<string> ListAdoptionReports(
        [Description("Optionaler Filter auf die Kategorie, z.B. 'Microsoft Teams user activity'. Leer lassen für alle.")]
        string? category = null)
    {
        var path = "/api/m365/reports/catalog";
        if (!string.IsNullOrWhiteSpace(category))
        {
            path += $"?category={Uri.EscapeDataString(category)}";
        }

        return await _apiServerClient.GetAsync(path);
    }

    [McpServerTool, Description("Ruft einen beliebigen Microsoft-365-Adoption-/Usage-Report (Microsoft Graph Beta, siehe ListAdoptionReports für gültige Namen) mit den für diesen Endpunkt zulässigen Parametern ab und liefert das Ergebnis als JSON.")]
    public async Task<string> GetAdoptionReport(
        [Description("Exakter Report-Name aus ListAdoptionReports, z.B. 'getOffice365ActiveUserDetail' oder 'getTeamsUserActivityCounts'.")]
        string reportName,
        [Description("Zeitraum D7/D30/D90/D180 - nur bei Reports, die 'period' unterstützen. Standard D30 falls weder period noch date angegeben.")]
        string? period = null,
        [Description("Stichtag YYYY-MM-DD - nur bei '...Detail'-Reports, die period ODER date akzeptieren. Nicht gemeinsam mit period angeben.")]
        string? date = null,
        [Description("Nur für 'getApiUsage': z.B. 'Exchange', 'SharePoint', 'OneDriveForBusiness', 'MicrosoftTeams', 'Yammer'.")]
        string? serviceArea = null,
        [Description("Nur für 'getApiUsage': Azure-AD-App-ID, auf die gefiltert werden soll.")]
        string? appId = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(period)) query.Add($"period={Uri.EscapeDataString(period)}");
        if (!string.IsNullOrWhiteSpace(date)) query.Add($"date={Uri.EscapeDataString(date)}");
        if (!string.IsNullOrWhiteSpace(serviceArea)) query.Add($"serviceArea={Uri.EscapeDataString(serviceArea)}");
        if (!string.IsNullOrWhiteSpace(appId)) query.Add($"appId={Uri.EscapeDataString(appId)}");

        var path = $"/api/m365/reports/{Uri.EscapeDataString(reportName)}";
        if (query.Count > 0)
        {
            path += $"?{string.Join("&", query)}";
        }

        return await _apiServerClient.GetAsync(path);
    }
}
