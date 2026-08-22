using System.Net.Http.Headers;
using McpServer.Auth;
using McpServer.Reporting;
using McpServer.Utils;

namespace McpServer.Services;

/// <summary>
/// Ruft Microsoft-Graph-Reports-Endpunkte auf und liefert IMMER JSON zurück, unabhängig davon,
/// ob Graph selbst JSON oder CSV liefert:
///
///  1. Versucht zuerst den BETA-Endpunkt mit "$format=application/json" (für getOffice365ActiveUserDetail
///     laut Microsoft-Dokumentation offiziell unterstützt: 200 OK mit JSON-Body statt Redirect).
///  2. Liefert Graph stattdessen CSV zurück (direkt als 200 OK/octet-stream ODER per 302-Redirect auf eine
///     temporäre, bereits vorautorisierte Download-URL - je nach Endpunkt/Version unterschiedlich), wird das
///     CSV automatisch heruntergeladen und über CsvJsonConverter in JSON umgewandelt.
///
/// Wichtig: Beta-Endpunkte gelten laut Microsoft als "subject to change" und sind nicht für
/// Produktion supported - dieser Wrapper macht das Verhalten dadurch UNABHÄNGIG davon nutzbar,
/// da JSON so oder so am Ende garantiert JSON zurückgibt.
/// </summary>
public class GraphReportsClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApiTokenService _tokenService;

    public GraphReportsClient(IHttpClientFactory httpClientFactory, ApiTokenService tokenService)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
    }

    /// <param name="reportPathWithoutQuery">
    /// Pfad + OData-Funktionsaufruf ohne führenden Host und ohne $format-Parameter,
    /// z.B. "/reports/getOffice365ActiveUserDetail(period='D30')".
    /// </param>
    public async Task<string> GetReportAsJsonAsync(string reportPathWithoutQuery)
    {
        var token = await _tokenService.GetGraphAppOnlyTokenAsync();

        // Schritt 1: Beta-Endpunkt mit explizitem JSON-Format anfragen, Redirects NICHT automatisch folgen,
        // damit wir kontrolliert zwischen "ist schon JSON" und "muss CSV nachladen" unterscheiden können.
        var noRedirectClient = _httpClientFactory.CreateClient("GraphNoRedirect");
        noRedirectClient.BaseAddress = new Uri("https://graph.microsoft.com/beta");
        noRedirectClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var separator = reportPathWithoutQuery.Contains('?') ? "&" : "?";
        var jsonAttemptUrl = $"{reportPathWithoutQuery}{separator}$format=application/json";

        var response = await noRedirectClient.GetAsync(jsonAttemptUrl);

        // Fall A: Graph liefert direkt JSON (dokumentiertes Verhalten für getOffice365ActiveUserDetail).
        if (response.IsSuccessStatusCode &&
            response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
        {
            return await response.Content.ReadAsStringAsync();
        }

        // Fall B: 302-Redirect auf eine vorautorisierte CSV-Download-URL (kein Authorization-Header nötig/erlaubt).
        if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is not null)
        {
            var downloadClient = _httpClientFactory.CreateClient(); // ohne Auth-Header
            var csv = await downloadClient.GetStringAsync(response.Headers.Location);
            return CsvJsonConverter.ConvertToJsonArray(csv);
        }

        // Fall C: 200 OK, aber Content-Type ist CSV/octet-stream statt JSON - direkt parsen.
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            return CsvJsonConverter.ConvertToJsonArray(body);
        }

        response.EnsureSuccessStatusCode(); // löst aussagekräftige Exception mit Statuscode aus
        return string.Empty; // unreachable
    }

    /// <summary>
    /// Ruft einen Report aus dem ReportCatalog anhand seines ParamMode korrekt parametrisiert auf.
    /// Validiert dabei die in der jeweiligen Graph-Beta-Dokumentation vorgesehenen Parameterkombinationen.
    /// </summary>
    public Task<string> GetCatalogReportAsJsonAsync(
        string functionName,
        string? period = null,
        string? date = null,
        string? serviceArea = null,
        string? appId = null)
    {
        if (!ReportCatalog.Reports.TryGetValue(functionName, out var def))
        {
            throw new ArgumentException(
                $"Unbekannter Report '{functionName}'. Nutze das Tool 'ListAdoptionReports', um alle verfügbaren Namen zu sehen.");
        }

        string path;

        switch (def.ParamMode)
        {
            case ReportParamMode.PeriodOrDate:
                if (!string.IsNullOrWhiteSpace(period) && !string.IsNullOrWhiteSpace(date))
                {
                    throw new ArgumentException($"'{functionName}' akzeptiert entweder period ODER date, nicht beides.");
                }
                if (!string.IsNullOrWhiteSpace(date))
                {
                    path = $"/reports/{def.FunctionName}(date={date})";
                }
                else
                {
                    var p = ValidatePeriod(period ?? "D30", functionName);
                    path = $"/reports/{def.FunctionName}(period='{p}')";
                }
                break;

            case ReportParamMode.PeriodOnly:
                var periodOnly = ValidatePeriod(period ?? "D30", functionName);
                path = $"/reports/{def.FunctionName}(period='{periodOnly}')";
                break;

            case ReportParamMode.None:
                path = $"/reports/{def.FunctionName}";
                break;

            case ReportParamMode.Special when def.FunctionName == "getApiUsage":
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(period)) parts.Add($"period='{ValidatePeriod(period, functionName)}'");
                if (!string.IsNullOrWhiteSpace(serviceArea)) parts.Add($"serviceArea='{serviceArea}'");
                if (!string.IsNullOrWhiteSpace(appId)) parts.Add($"appId='{appId}'");
                path = parts.Count > 0 ? $"/reports/{def.FunctionName}({string.Join(",", parts)})" : $"/reports/{def.FunctionName}";
                break;

            default:
                throw new NotSupportedException($"ParamMode '{def.ParamMode}' für '{functionName}' ist nicht implementiert.");
        }

        return GetReportAsJsonAsync(path);
    }

    private static string ValidatePeriod(string period, string functionName)
    {
        if (!ReportCatalog.AllowedPeriods.Contains(period, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Ungültiger period-Wert '{period}' für '{functionName}'. Erlaubt: {string.Join(", ", ReportCatalog.AllowedPeriods)}.");
        }
        return period.ToUpperInvariant();
    }
}
