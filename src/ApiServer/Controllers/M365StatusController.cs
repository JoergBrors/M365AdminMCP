using ApiServer.Auth;
using ApiServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiServer.Controllers;

[ApiController]
[Route("api/m365/status")]
[Authorize(Policy = PolicyNames.TasksReadWrite)]
public class M365StatusController : ControllerBase
{
    private readonly GraphApiClient _graphApiClient;

    public M365StatusController(GraphApiClient graphApiClient)
    {
        _graphApiClient = graphApiClient;
    }

    [HttpGet("health-overviews")]
    public async Task<ContentResult> GetServiceHealthOverview(CancellationToken cancellationToken)
    {
        return Json(await _graphApiClient.GetServiceHealthOverviewAsync(cancellationToken));
    }

    [HttpGet("health-overviews/degraded")]
    public async Task<ContentResult> GetDegradedServiceHealthDetails(CancellationToken cancellationToken)
    {
        return Json(await _graphApiClient.GetDegradedServiceHealthDetailsAsync(cancellationToken));
    }

    [HttpGet("health-overviews/{serviceHealthId}")]
    public async Task<ContentResult> GetServiceHealthDetail(
        string serviceHealthId,
        [FromQuery] bool expandIssues = true,
        [FromQuery] bool includeResolvedIssues = false,
        CancellationToken cancellationToken = default)
    {
        return Json(await _graphApiClient.GetServiceHealthDetailAsync(
            serviceHealthId,
            expandIssues,
            includeResolvedIssues,
            cancellationToken));
    }

    [HttpGet("issues")]
    public async Task<ContentResult> GetServiceHealthIssues(CancellationToken cancellationToken)
    {
        return Json(await _graphApiClient.GetServiceHealthIssuesAsync(cancellationToken));
    }

    private static ContentResult Json(string json) => new()
    {
        Content = json,
        ContentType = "application/json"
    };
}
