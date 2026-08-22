using System.ComponentModel;
using System.Text.Json;
using McpServer.Reporting;
using McpServer.Services;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Voller Zugriff auf ALLE Microsoft Graph BETA Adoption-/Usage-Report-Endpunkte (siehe
/// Reporting/ReportCatalog.cs) über zwei generische Tools statt ~90 einzelner Methoden:
/// erst auflisten, dann gezielt mit den für den jeweiligen Endpunkt gültigen Parametern abrufen.
/// Alle Aufrufe laufen über GraphReportsClient und liefern deshalb garantiert JSON zurück
/// (Beta-JSON-Versuch, sonst automatischer CSV-Fallback).
///
/// Benötigt Application Permission "Reports.Read.All" (Admin Consent) auf Microsoft Graph.
/// </summary>
[McpServerToolType]
public class M365AdoptionCatalogTool
{
    private readonly GraphReportsClient _reportsClient;

    public M365AdoptionCatalogTool(GraphReportsClient reportsClient)
    {
        _reportsClient = reportsClient;
    }

    [McpServerTool, Description("Listet alle verfügbaren Microsoft-365-Adoption-/Usage-Report-Endpunkte (Microsoft Graph Beta) mit Kategorie, Beschreibung und benötigten Parametern auf.")]
    public string ListAdoptionReports(
        [Description("Optionaler Filter auf die Kategorie, z.B. 'Microsoft Teams user activity'. Leer lassen für alle.")]
        string? category = null)
    {
        var reports = ReportCatalog.Reports.Values
            .Where(r => string.IsNullOrWhiteSpace(category) || string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Category).ThenBy(r => r.FunctionName)
            .Select(r => new
            {
                name = r.FunctionName,
                category = r.Category,
                description = r.Description,
                parameters = r.ParamMode switch
                {
                    ReportParamMode.PeriodOrDate => "period (D7/D30/D90/D180) ODER date (YYYY-MM-DD) - genau eines von beiden",
                    ReportParamMode.PeriodOnly => "period (D7/D30/D90/D180), Standard D30",
                    ReportParamMode.None => "keine Parameter (aktueller Zustand)",
                    ReportParamMode.Special => "period (optional), serviceArea (optional), appId (optional)",
                    _ => "unbekannt"
                }
            });

        return JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true });
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
        return await _reportsClient.GetCatalogReportAsJsonAsync(reportName, period, date, serviceArea, appId);
    }
}
