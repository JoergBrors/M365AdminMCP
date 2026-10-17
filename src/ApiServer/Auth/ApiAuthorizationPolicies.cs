using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace ApiServer.Auth;

/// <summary>
/// Autorisierungs-Policies des ApiServers (Finding F-016, ADR-0006).
///
/// <see cref="PolicyNames.DataAccess"/> schuetzt alle Daten-Controller (Status, Messages, Reports, Health) und erlaubt AUSSCHLIESSLICH
/// die App-Rolle "Tasks.ReadWrite.All" (App-only, nur der McpServer hat sie). Ein delegierter Nutzer-Token mit dem Scope
/// "Tasks.ReadWrite" reicht hier bewusst NICHT: Der Scope ist von Nutzern selbst bestaetigbar und wuerde sonst jedem Tenant-Nutzer
/// den Direktzugriff auf tenantweite Admin-Daten ohne MCP und ohne "Mcp.Access" ermoeglichen.
///
/// <see cref="PolicyNames.TasksReadWrite"/> (Rolle ODER Scope) bleibt nur fuer den Task-Demo-Controller.
/// </summary>
public static class ApiAuthorizationPolicies
{
    public const string AppRole = "Tasks.ReadWrite.All";
    public const string DelegatedScope = "Tasks.ReadWrite";

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.TasksReadWrite, policy =>
            policy.RequireAssertion(context => HasAppRole(context.User) || HasDelegatedScope(context.User)));

        options.AddPolicy(PolicyNames.DataAccess, policy =>
            policy.RequireAssertion(context => HasAppRole(context.User)));
    }

    public static bool HasAppRole(ClaimsPrincipal user) =>
        user.Claims.Any(c => (c.Type == "roles" || c.Type == ClaimTypes.Role) && c.Value == AppRole);

    public static bool HasDelegatedScope(ClaimsPrincipal user) =>
        user.Claims.Any(c =>
            (c.Type == "scp" || c.Type == "http://schemas.microsoft.com/identity/claims/scope") &&
            c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(DelegatedScope));
}
