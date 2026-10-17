using System.Diagnostics;
using McpServer.Auth;

namespace ApiServer.Auth;

/// <summary>
/// Schreibt den vom McpServer uebermittelten, validierten UserContext in den Log-Scope und die Activity (Audit).
/// Hat KEINEN Einfluss auf die Autorisierung: die steht vorher fest (Policy "DataAccess"/"TasksReadWrite").
/// </summary>
public static class UserContextAudit
{
    public static WebApplication UseUserContextAudit(this WebApplication app)
    {
        var includeUpn = app.Configuration.GetValue("Audit:LogUserPrincipalName", true);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("McpUserAudit");

        app.Use(async (context, next) =>
        {
            // /healthz und Anfragen ohne Header erzeugen keinen Audit-Eintrag
            var hasHeader = context.Request.Headers.ContainsKey(UserContext.MethodHeader);
            if (!hasHeader)
            {
                await next();
                return;
            }

            var user = UserContext.FromHeaders(context.Request.Headers);
            var upn = includeUpn ? user.PreferredUsername : null;
            var scope = new Dictionary<string, object?>
            {
                ["McpAuthMethod"] = user.AuthMethod,
                ["McpUserOid"] = user.Oid,
                ["McpUserTid"] = user.Tid,
                ["McpUserUpn"] = upn
            };

            var activity = Activity.Current;
            activity?.SetTag("mcp.auth_method", user.AuthMethod);
            activity?.SetTag("mcp.user_oid", user.Oid);
            activity?.SetTag("mcp.user_tid", user.Tid);

            using (logger.BeginScope(scope))
            {
                logger.LogInformation(
                    "MCP caller authMethod={AuthMethod} oid={Oid} tid={Tid} upn={Upn} {Method} {Path}",
                    user.AuthMethod, user.Oid, user.Tid, upn, context.Request.Method, context.Request.Path.Value);
                await next();
            }
        });

        return app;
    }
}
