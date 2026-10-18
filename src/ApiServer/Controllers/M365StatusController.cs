using ApiServer.Auth;
using ApiServer.Health;
using ApiServer.Services;
using McpServer.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ApiServer.Controllers;

[ApiController]
[Route("api/m365/status")]
[Authorize(Policy = PolicyNames.DataAccess)]
public class M365StatusController : ControllerBase
{
    private const string SizeHint =
        "Antwort zu gross. Fuer Issues search_service_health_issues (kompakt, mit Filtern/Pagination), get_service_health_issue und " +
        "get_service_health_issue_content (vollstaendiger Inhalt in Teilen) verwenden. Dieses Tool liest nur die erste Graph-Seite; vollstaendige Listen liefert die Suche.";

    private readonly GraphApiClient _graphApiClient;
    private readonly ServiceHealthIssueService _issues;
    private readonly ResponseLimitsOptions _limits;

    public M365StatusController(GraphApiClient graphApiClient, ServiceHealthIssueService issues, IOptions<ResponseLimitsOptions> limits)
    {
        _graphApiClient = graphApiClient;
        _issues = issues;
        _limits = limits.Value;
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

    [HttpGet("issues/search")]
    public async Task<IActionResult> SearchIssues(
        [FromQuery] string? service = null, [FromQuery] bool? isResolved = null, [FromQuery] string? classification = null,
        [FromQuery] string? status = null, [FromQuery] string? startFrom = null, [FromQuery] string? startTo = null,
        [FromQuery] string? searchText = null, [FromQuery] int? maxResults = null, [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!IssueInputValidator.TryNormalizeText(service, "service", out var svc, out var error) ||
            !IssueInputValidator.TryParseClassification(classification, out var cls, out error) ||
            !IssueInputValidator.TryParseStatus(status, out var st, out error) ||
            !IssueInputValidator.TryParseDate(startFrom, "startFrom", out var from, out error) ||
            !IssueInputValidator.TryParseDate(startTo, "startTo", out var to, out error) ||
            !IssueInputValidator.TryNormalizeText(searchText, "searchText", out var text, out error) ||
            !IssueInputValidator.TryNormalizeMaxResults(maxResults, out var max, out error))
        {
            return BadRequest(new { error });
        }

        var query = new IssueQuery(svc, isResolved, cls, st, from, to, text, max);
        return Json(await _issues.SearchAsync(query, continuationToken, cancellationToken), guard: false);
    }

    [HttpGet("issues/content")]
    public async Task<IActionResult> GetIssueContent(
        [FromQuery] string contentId, [FromQuery] string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        if (!IssueInputValidator.TryParseContentId(contentId, out _, out _, out _, out var error))
        {
            return BadRequest(new { error });
        }

        return Json(await _issues.GetContentAsync(contentId, continuationToken, cancellationToken), guard: false);
    }

    [HttpGet("issues/{id}")]
    public async Task<IActionResult> GetIssue(string id, [FromQuery] string? projection = null, CancellationToken cancellationToken = default)
    {
        if (!IssueInputValidator.TryParseIssueId(id, out var issueId, out var error) ||
            !IssueInputValidator.TryParseProjection(projection, out var proj, out error))
        {
            return BadRequest(new { error });
        }

        return Json(await _issues.GetIssueAsync(issueId, proj, cancellationToken), guard: false);
    }

    private ContentResult Json(string json, bool guard = true) => new()
    {
        Content = guard ? ResponseSizeGuard.Enforce(json, _limits.MaxResponseBytes, "microsoftGraph.serviceAnnouncement", SizeHint) : json,
        ContentType = "application/json"
    };
}
