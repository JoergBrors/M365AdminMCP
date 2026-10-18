using ApiServer.Auth;
using ApiServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ApiServer.Controllers;

[ApiController]
[Route("api/m365/messages")]
[Authorize(Policy = PolicyNames.DataAccess)]
public class M365MessagesController : ControllerBase
{
    private readonly GraphApiClient _graphApiClient;

    private readonly ResponseLimitsOptions _limits;

    public M365MessagesController(GraphApiClient graphApiClient, IOptions<ResponseLimitsOptions> limits)
    {
        _graphApiClient = graphApiClient;
        _limits = limits.Value;
    }

    [HttpGet]
    public async Task<ContentResult> GetMessageCenterMessages([FromQuery] string? odataFilter, CancellationToken cancellationToken)
    {
        var json = await _graphApiClient.GetMessageCenterMessagesAsync(odataFilter, cancellationToken);
        return new ContentResult
        {
            Content = ResponseSizeGuard.Enforce(json, _limits.MaxResponseBytes, "microsoftGraph.serviceAnnouncement.messages",
                "Antwort zu gross. odataFilter verwenden (z. B. category eq 'stayInformed') oder microsoft_service_messages mit maxResults/sinceUtc."),
            ContentType = "application/json"
        };
    }
}
