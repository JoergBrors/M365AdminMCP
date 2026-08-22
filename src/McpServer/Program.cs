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

// MCP Server mit HTTP/SSE-Transport (passend für Hosting auf Azure App Service).
// HINWEIS: Preview-API, siehe Kommentar in Tools/TasksTool.cs.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<TasksTool>()
    .WithTools<M365StatusTool>()
    .WithTools<M365MessagesTool>()
    .WithTools<M365AdoptionTool>()
    .WithTools<M365AdoptionCatalogTool>();

var app = builder.Build();

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

var mcpEndpoint = app.MapMcp(); // registriert die MCP-Endpunkte (SSE/HTTP)
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
        var resource = GetMcpResource(configuration);
        var tenantId = configuration["AzureAd:TenantId"];
        var scope = GetMcpScope(configuration);

        var metadata = new
        {
            resource,
            authorization_servers = new[]
            {
                $"https://login.microsoftonline.com/{tenantId}/v2.0"
            },
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

static string GetMcpResource(IConfiguration configuration)
{
    var audience = configuration["AzureAd:Audience"];
    if (!string.IsNullOrWhiteSpace(audience))
    {
        return audience;
    }

    var scope = GetMcpScope(configuration);
    var scopeSeparator = scope.LastIndexOf('/');
    return scopeSeparator > 0 ? scope[..scopeSeparator] : scope;
}

static void SetNoStoreHeaders(HttpResponse response)
{
    response.Headers.CacheControl = "no-store, no-cache, max-age=0";
    response.Headers.Pragma = "no-cache";
    response.Headers.Expires = "0";
}
