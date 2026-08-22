using ApiServer.Auth;
using ApiServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiServer.Controllers;

[ApiController]
[Route("api/m365/messages")]
[Authorize(Policy = PolicyNames.TasksReadWrite)]
public class M365MessagesController : ControllerBase
{
    private readonly GraphApiClient _graphApiClient;

    public M365MessagesController(GraphApiClient graphApiClient)
    {
        _graphApiClient = graphApiClient;
    }

    [HttpGet]
    public async Task<ContentResult> GetMessageCenterMessages([FromQuery] string? odataFilter, CancellationToken cancellationToken)
    {
        var json = await _graphApiClient.GetMessageCenterMessagesAsync(odataFilter, cancellationToken);
        return new ContentResult
        {
            Content = json,
            ContentType = "application/json"
        };
    }
}
