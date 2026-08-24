using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using McpServer.Auth;
using McpServer.Services;
using McpServer.Tools;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

builder.Services.Configure<AzureAdOptions>(builder.Configuration.GetSection("AzureAd"));
builder.Services.Configure<McpAuthOptions>(builder.Configuration.GetSection("McpAuth"));
builder.Services.AddSingleton<ApiTokenService>();
builder.Services.AddSingleton<ApiServerClient>();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// Manche MCP-Clients (u.a. Copilot Studios "Dynamisch mit Ermittlung") fuehren die OAuth-
// Discovery browserseitig aus dem Maker-Portal heraus aus - ohne CORS scheitert der Preflight
// (OPTIONS) bereits im Browser, bevor ueberhaupt ein Request am Server ankommt bzw. geloggt wird.
builder.Services.AddCors(options =>
{
    options.AddPolicy("McpDiscovery", policy => policy
        .AllowAnyOrigin()
        .WithMethods("GET", "POST", "OPTIONS")
        .WithHeaders("Content-Type", "Authorization"));
});

var requireMcpAuthentication = builder.Configuration.GetValue("McpAuth:RequireAuthentication", true);
if (requireMcpAuthentication)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

    builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var mcpClientId = builder.Configuration["AzureAd:ClientId"];
        var mcpAudience = builder.Configuration["AzureAd:Audience"];

        options.TokenValidationParameters.ValidAudiences = new[]
        {
            mcpAudience,
            mcpClientId
        }.Where(audience => !string.IsNullOrWhiteSpace(audience));

        options.Events ??= new JwtBearerEvents();
        options.Events.OnChallenge = context =>
        {
            context.HandleResponse();

            if (!context.Response.HasStarted)
            {
                var baseUrl = GetExternalBaseUrl(context.HttpContext, builder.Configuration);
                var scope = GetMcpScope(builder.Configuration);
                var resourceMetadataUrl = $"{baseUrl}/.well-known/oauth-protected-resource";

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                SetNoStoreHeaders(context.Response);
                context.Response.Headers.WWWAuthenticate =
                    $"Bearer resource_metadata=\"{resourceMetadataUrl}\", scope=\"{scope}\"";
            }

            return Task.CompletedTask;
        };
    });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("McpAccess", policy =>
            policy.RequireAssertion(context =>
                context.User.Claims.Any(c =>
                    (c.Type == "scp" || c.Type == "http://schemas.microsoft.com/identity/claims/scope") &&
                    c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("Mcp.Access")) ||
                context.User.Claims.Any(c =>
                    (c.Type == "roles" || c.Type == ClaimTypes.Role) &&
                    c.Value == "Mcp.Access")));
    });
}

// MCP Server mit Streamable HTTP Transport (passend fuer Hosting auf Azure App Service).
// Stateless (SDK-Default seit v2): keine Mcp-Session-Id, kein Session-Affinity-Bedarf bei
// mehreren App-Service-Instanzen. Legacy-SSE (/sse, /message) ist bewusst NICHT aktiviert -
// ChatGPT, Claude und Copilot Studio sprechen alle bereits Streamable HTTP, und
// EnableLegacySse=true wirft bei Stateless=true (Default) eine InvalidOperationException.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<TasksTool>()
    .WithTools<M365StatusTool>()
    .WithTools<M365MessagesTool>()
    .WithTools<M365AdoptionTool>()
    .WithTools<M365AdoptionCatalogTool>();

var app = builder.Build();

app.UseCors("McpDiscovery");

if (requireMcpAuthentication)
{
    app.UseAuthentication();
    app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated != true &&
            context.Request.Headers.TryGetValue("X-MCP-API-Key", out var apiKeyHeader))
        {
            var configuredApiKey = app.Configuration["McpAuth:ApiKey"];
            if (!string.IsNullOrWhiteSpace(configuredApiKey) &&
                string.Equals(apiKeyHeader.ToString(), configuredApiKey, StringComparison.Ordinal))
            {
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "mcp-api-key-client"),
                    new Claim(ClaimTypes.Role, "Mcp.Access")
                };
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "McpApiKey"));
            }
        }

        await next();
    });
    app.UseAuthorization();
}

MapOAuthProtectedResourceMetadata(app);
app.MapOAuthProxyEndpoints();

var mcpEndpoint = app.MapMcp("/mcp"); // einziger MCP-Endpunkt: Streamable HTTP (stateless)
if (requireMcpAuthentication)
{
    mcpEndpoint.RequireAuthorization("McpAccess");
}

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

app.Run();

static void MapOAuthProtectedResourceMetadata(WebApplication app)
{
    static IResult Metadata(HttpContext context, IConfiguration configuration)
    {
        SetNoStoreHeaders(context.Response);

        var baseUrl = GetExternalBaseUrl(context, configuration);
        // RFC 8707/9728: "resource" MUSS die kanonische URL des MCP-Servers selbst sein
        // (https://.../mcp), NICHT die Entra-Audience (api://...). Manche Clients (z.B.
        // Claude) verwerfen sonst die externen authorization_servers und raten stattdessen
        // einen eigenen /authorize-Endpunkt auf dem MCP-Server-Origin.
        var resource = $"{baseUrl}/mcp";
        var scope = GetMcpScope(configuration);

        var metadata = new
        {
            resource,
            // Zeigt auf die eigene OAuthProxy-Fassade (siehe Auth/OAuthProxy.cs), NICHT direkt
            // auf Entra: Entra implementiert RFC 8707 nicht und lehnt den von RFC-8707-konformen
            // Clients (z.B. Claude) mitgesendeten "resource"-Parameter mit AADSTS9010010 ab. Die
            // Fassade entfernt "resource" transparent, bevor sie an Entra weiterleitet.
            authorization_servers = new[] { baseUrl },
            bearer_methods_supported = new[] { "header" },
            scopes_supported = new[]
            {
                scope
            }
        };

        return Results.Json(metadata);
    }

    app.MapGet("/.well-known/oauth-protected-resource",
        (HttpContext context, IConfiguration configuration) => Metadata(context, configuration))
        .AllowAnonymous();

    app.MapGet("/.well-known/oauth-protected-resource/mcp",
        (HttpContext context, IConfiguration configuration) => Metadata(context, configuration))
        .AllowAnonymous();
}

static string GetExternalBaseUrl(HttpContext context, IConfiguration configuration)
{
    var configuredBaseUrl = configuration["McpAuth:ExternalBaseUrl"];
    if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
    {
        return configuredBaseUrl.TrimEnd('/');
    }

    return $"{context.Request.Scheme}://{context.Request.Host}".TrimEnd('/');
}

static string GetMcpScope(IConfiguration configuration)
{
    var configuredScope = configuration["McpAuth:Scope"];
    if (!string.IsNullOrWhiteSpace(configuredScope))
    {
        return configuredScope;
    }

    var audience = configuration["AzureAd:Audience"];
    return string.IsNullOrWhiteSpace(audience)
        ? "Mcp.Access"
        : $"{audience}/Mcp.Access";
}


static void SetNoStoreHeaders(HttpResponse response)
{
    response.Headers.CacheControl = "no-store, no-cache, max-age=0";
    response.Headers.Pragma = "no-cache";
    response.Headers.Expires = "0";
}
