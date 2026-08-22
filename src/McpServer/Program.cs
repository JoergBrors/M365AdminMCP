using McpServer.Auth;
using McpServer.Services;
using McpServer.Tools;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

builder.Services.Configure<AzureAdOptions>(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddSingleton<ApiTokenService>();
builder.Services.AddSingleton<ApiServerClient>();
builder.Services.AddHttpClient();

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
