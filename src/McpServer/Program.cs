using McpServer.Auth;
using McpServer.Services;
using McpServer.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AzureAdOptions>(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddSingleton<ApiTokenService>();
builder.Services.AddSingleton<GraphReportsClient>();
builder.Services.AddHttpClient();

// Eigener Named Client für den Reports-Wrapper: KEINE automatische Redirect-Verfolgung,
// damit GraphReportsClient selbst entscheiden kann, ob ein 302 (CSV-Download-URL) vorliegt,
// bevor irgendwelche Header (insb. Authorization) an einen fremden Host weitergereicht würden.
builder.Services.AddHttpClient("GraphNoRedirect")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

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

app.MapMcp(); // registriert die MCP-Endpunkte (SSE/HTTP)

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

app.Run();
