using System.Text.Json;
using ApiServer.Auth;
using ApiServer.Services;
using McpServer.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiServer.Controllers;

[ApiController]
[Route("api/m365/reports")]
[Authorize(Policy = PolicyNames.TasksReadWrite)]
public class M365ReportsController : ControllerBase
{
    private readonly GraphApiClient _graphApiClient;

    public M365ReportsController(GraphApiClient graphApiClient)
    {
        _graphApiClient = graphApiClient;
    }

    [HttpGet("catalog")]
    public IActionResult ListAdoptionReports([FromQuery] string? category = null)
    {
        var reports = ReportCatalog.Reports.Values
            .Where(r => string.IsNullOrWhiteSpace(category) || string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Category)
            .ThenBy(r => r.FunctionName)
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

        return Content(JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }), "application/json");
    }

    [HttpGet("{reportName}")]
    public async Task<ContentResult> GetAdoptionReport(
        string reportName,
        [FromQuery] string? period = null,
        [FromQuery] string? date = null,
        [FromQuery] string? serviceArea = null,
        [FromQuery] string? appId = null,
        CancellationToken cancellationToken = default)
    {
        var json = await _graphApiClient.GetCatalogReportAsJsonAsync(
            reportName,
            period,
            date,
            serviceArea,
            appId,
            cancellationToken);

        return new ContentResult
        {
            Content = json,
            ContentType = "application/json"
        };
    }
}
