using ApiServer.Auth;
using ApiServer.Health;
using McpServer.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ApiServer.Controllers;

/// <summary>
/// Intune-/Microsoft-365-Service-Health (Graph) und Azure Service/Resource Health (ARM). Nur lesend.
/// Eingaben werden verbindlich validiert (400); Fehler der Upstream-APIs werden als strukturierte Antwort (status/errors) geliefert.
/// </summary>
[ApiController]
[Route("api/microsoft-health")]
[Authorize(Policy = PolicyNames.TasksReadWrite)]
public class MicrosoftHealthController : ControllerBase
{
    private readonly MicrosoftServiceHealthClient _graph;
    private readonly AzureResourceHealthClient _azure;
    private readonly MicrosoftHealthSummaryService _summary;
    private readonly MicrosoftHealthOptions _options;

    public MicrosoftHealthController(
        MicrosoftServiceHealthClient graph,
        AzureResourceHealthClient azure,
        MicrosoftHealthSummaryService summary,
        IOptions<MicrosoftHealthOptions> options)
    {
        _graph = graph;
        _azure = azure;
        _summary = summary;
        _options = options.Value;
    }

    [HttpGet("intune/health")]
    public async Task<IActionResult> IntuneHealth(CancellationToken cancellationToken) =>
        Ok(await _graph.GetIntuneHealthAsync(cancellationToken));

    [HttpGet("intune/incidents")]
    public async Task<IActionResult> IntuneIncidents([FromQuery] bool activeOnly = false, [FromQuery] int? maxResults = null, CancellationToken cancellationToken = default)
    {
        if (!HealthInputValidator.TryNormalizeMaxResults(maxResults, _options.DefaultMaxResults, _options.MaxResults, out var max, out var error))
            return BadRequest(new { error });

        return Ok(await _graph.GetIntuneIncidentsAsync(activeOnly, max, cancellationToken));
    }

    [HttpGet("messages")]
    public async Task<IActionResult> Messages(
        [FromQuery] bool onlyIntune = true, [FromQuery] string? sinceUtc = null, [FromQuery] string? serviceName = null,
        [FromQuery] int? maxResults = null, CancellationToken cancellationToken = default)
    {
        if (!HealthInputValidator.TryParseSinceUtc(sinceUtc, DateTime.UtcNow, out var since, out var error) ||
            !HealthInputValidator.TryNormalizeMaxResults(maxResults, _options.DefaultMaxResults, _options.MaxResults, out var max, out error))
            return BadRequest(new { error });

        if (serviceName is { Length: > 100 })
            return BadRequest(new { error = "serviceName ist zu lang (max. 100 Zeichen)." });

        return Ok(await _graph.GetMessagesAsync(onlyIntune, since, serviceName, max, cancellationToken));
    }

    [HttpGet("azure/events")]
    public async Task<IActionResult> AzureEvents(
        [FromQuery] string? subscriptionId, [FromQuery] string? sinceUtc = null, [FromQuery] int? maxResults = null, CancellationToken cancellationToken = default)
    {
        if (!HealthInputValidator.TryParseSubscriptionId(subscriptionId, out var normalized, out var error) ||
            !HealthInputValidator.TryParseSinceUtc(sinceUtc, DateTime.UtcNow, out var since, out error) ||
            !HealthInputValidator.TryNormalizeMaxResults(maxResults, _options.DefaultMaxResults, _options.MaxResults, out var max, out error))
            return BadRequest(new { error });

        return Ok(await _azure.GetServiceHealthEventsAsync(normalized, since, max, cancellationToken));
    }

    [HttpGet("azure/resource")]
    public async Task<IActionResult> AzureResource([FromQuery] string? resourceId, CancellationToken cancellationToken)
    {
        if (!HealthInputValidator.TryParseResourceId(resourceId, out var normalized, out _, out var error))
            return BadRequest(new { error });

        return Ok(await _azure.GetResourceAvailabilityAsync(normalized, cancellationToken));
    }

    [HttpGet("azure/subscription-resources")]
    public async Task<IActionResult> AzureSubscriptionResources(
        [FromQuery] string? subscriptionId, [FromQuery] int? maxResults = null, CancellationToken cancellationToken = default)
    {
        if (!HealthInputValidator.TryParseSubscriptionId(subscriptionId, out var normalized, out var error) ||
            !HealthInputValidator.TryNormalizeMaxResults(maxResults, _options.DefaultMaxResults, _options.MaxResults, out var max, out error))
            return BadRequest(new { error });

        return Ok(await _azure.GetSubscriptionAvailabilityAsync(normalized, max, cancellationToken));
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await _summary.GetSummaryAsync(cancellationToken));
}
