using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace McpServer.Hardening;

/// <summary>
/// Konfigurierbare Request-Haertung (Abschnitt "Hardening"): Rate Limiting, maximale Request-Groesse, HSTS.
/// Defaults sind grosszuegig gewaehlt, damit bestehende MCP-Clients nicht beeintraechtigt werden.
/// Wird von McpServer und ApiServer genutzt (Compile-Link, siehe ADR-0009).
/// </summary>
public sealed class HardeningOptions
{
    public const string SectionName = "Hardening";

    public bool Enabled { get; set; } = true;

    /// <summary>Maximale Request-Body-Groesse (MCP- und OAuth-Requests sind klein).</summary>
    public long MaxRequestBodyBytes { get; set; } = 1_048_576;

    /// <summary>Allgemeines Limit je Aufrufer und Minute (authentifiziert: je Nutzer/Client, sonst je IP).</summary>
    public int PermitLimitPerMinute { get; set; } = 120;

    /// <summary>Strengeres Limit je IP und Minute fuer OAuth-Endpunkte.</summary>
    public int OAuthPermitLimitPerMinute { get; set; } = 30;

    public string[] OAuthPaths { get; set; } = ["/authorize", "/token", "/register"];

    public string[] ExemptPaths { get; set; } = ["/healthz"];
}

public static class RequestHardening
{
    public static HardeningOptions GetOptions(IConfiguration configuration) =>
        configuration.GetSection(HardeningOptions.SectionName).Get<HardeningOptions>() ?? new HardeningOptions();

    /// <summary>
    /// Client-IP hinter dem App-Service-Frontend: letzter Eintrag von X-Forwarded-For (vom Frontend angehaengt, vom Aufrufer
    /// nicht faelschbar - ein gefaelschter erster Eintrag wird ignoriert). Ohne Header: Remote-Adresse. Port wird entfernt.
    /// </summary>
    public static string GetClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last))
            {
                // "1.2.3.4:5678" -> "1.2.3.4"; IPv6 ("[::1]:80" oder "::1") unveraendert lassen, solange kein einzelner Doppelpunkt
                var colon = last.LastIndexOf(':');
                if (colon > 0 && last.IndexOf(':') == colon && last.Contains('.'))
                {
                    last = last[..colon];
                }

                return last;
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static string GetCallerKey(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var id = user.FindFirst("oid")?.Value ?? user.FindFirst("sub")?.Value
                     ?? user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value
                     ?? user.Identity.Name;
            if (!string.IsNullOrWhiteSpace(id))
            {
                return "user:" + id;
            }
        }

        return "ip:" + GetClientIp(context);
    }

    public static IServiceCollection AddRequestHardening(this IServiceCollection services, IConfiguration configuration)
    {
        var options = GetOptions(configuration);
        if (!options.Enabled)
        {
            return services;
        }

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (rejected, _) =>
            {
                rejected.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path;
                if (options.ExemptPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
                {
                    return RateLimitPartition.GetNoLimiter("exempt");
                }

                var isOAuth = options.OAuthPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
                var key = isOAuth ? "oauth:" + GetClientIp(context) : GetCallerKey(context);
                var permit = isOAuth ? options.OAuthPermitLimitPerMinute : options.PermitLimitPerMinute;

                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

        return services;
    }

    public static IWebHostBuilder ConfigureRequestLimits(this IWebHostBuilder webHost, IConfiguration configuration)
    {
        var options = GetOptions(configuration);
        if (options.Enabled)
        {
            webHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes);
        }

        return webHost;
    }

    /// <summary>Muss NACH UseAuthentication und VOR UseAuthorization stehen, damit auch abgelehnte (401) Anfragen limitiert werden.</summary>
    public static WebApplication UseRequestHardening(this WebApplication app)
    {
        var options = GetOptions(app.Configuration);
        if (!options.Enabled)
        {
            return app;
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseRateLimiter();
        return app;
    }
}
