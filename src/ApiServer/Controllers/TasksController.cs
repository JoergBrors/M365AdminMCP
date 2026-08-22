using ApiServer.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiServer.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = PolicyNames.TasksReadWrite)]
public class TasksController : ControllerBase
{
    // In-Memory-Demo-Daten – bewusst ohne Datenbank, siehe docs/ARCHITECTURE.md Punkt 7.
    private static readonly List<string> Tasks = new() { "Setup Entra ID", "Deploy MVP" };

    [HttpGet]
    public IActionResult Get()
    {
        var isAppOnly = User.HasClaim(c => c.Type == "roles");
        var callerType = isAppOnly ? "app-only" : "delegated";
        var caller = isAppOnly
            ? User.FindFirst("azp")?.Value ?? "unbekannte-app"
            : User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unbekannter-nutzer";

        return Ok(new
        {
            callerType,
            caller,
            tasks = Tasks
        });
    }

    [HttpPost]
    public IActionResult Add([FromBody] string task)
    {
        Tasks.Add(task);
        return CreatedAtAction(nameof(Get), new { }, task);
    }
}
