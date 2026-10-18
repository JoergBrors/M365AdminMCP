using System.Security.Claims;
using System.Text.Encodings.Web;
using ApiServer.Auth;
using ApiServer.Controllers;
using ApiServer.Health;
using ApiServer.Services;
using McpServer.Auth;
using McpServer.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace E2E.Host;

/// <summary>
/// Reproduzierbarer E2E-Aufbau ohne Azure: echte ApiServer-Controller/Services (simulierter Graph), echter McpServer
/// (Tool-Katalog, ApiServerClient, MCP-SDK-Serialisierung ueber Streamable HTTP/SSE) auf Loopback-Ports.
/// Nur die Token-Beschaffung (Entra) und Graph sind ersetzt.
/// </summary>
public sealed class E2eHost : IAsyncDisposable
{
    // Testwert, zur Laufzeit erzeugt (kein Schluessel-Literal im Quelltext).
    public static readonly string SigningKey = TestKey("e2e");

    public static string TestKey(string tag) => tag + "-test-only-" + new string('k', 40);

    private WebApplication? _api;
    private WebApplication? _mcp;

    public string ApiUrl { get; private set; } = "";
    public string McpUrl { get; private set; } = "";

    public static async Task<E2eHost> StartAsync(Func<HttpRequestMessage, HttpResponseMessage> graph, Action<ResponseLimitsOptions>? limits = null)
    {
        var host = new E2eHost();
        host._api = BuildApi(graph, limits);
        await host._api.StartAsync();
        host.ApiUrl = AddressOf(host._api);

        host._mcp = BuildMcp(host.ApiUrl);
        await host._mcp.StartAsync();
        host.McpUrl = AddressOf(host._mcp) + "/mcp";
        return host;
    }

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("[::]", "127.0.0.1");

    private static WebApplication BuildApi(Func<HttpRequestMessage, HttpResponseMessage> graph, Action<ResponseLimitsOptions>? limits)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();

        b.Services.AddControllers().AddApplicationPart(typeof(M365StatusController).Assembly);
        b.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        b.Services.AddAuthorization(ApiAuthorizationPolicies.Configure);

        b.Services.ConfigureHttpClientDefaults(c => c.ConfigurePrimaryHttpMessageHandler(() => new GraphStub(graph)));
        b.Services.Configure<GraphAuthOptions>(o => o.UseManagedIdentity = true);
        b.Services.AddSingleton<GraphTokenService, FakeGraphToken>();
        b.Services.AddSingleton<GraphApiClient>();
        b.Services.AddOptions<MicrosoftHealthOptions>();
        b.Services.AddSingleton<HealthRequestRunner>();
        b.Services.AddOptions<ResponseLimitsOptions>().Configure(o => { o.ContinuationSigningKey = SigningKey; limits?.Invoke(o); });
        b.Services.AddSingleton<ContinuationTokenService>();
        b.Services.AddSingleton<ServiceHealthIssueService>();

        var app = b.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    private static WebApplication BuildMcp(string apiUrl)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ApiServer:BaseUrl"] = apiUrl });

        b.Services.AddHttpContextAccessor();
        b.Services.AddHttpClient();
        b.Services.Configure<AzureAdOptions>(o => o.UseManagedIdentity = true);
        b.Services.AddSingleton<ApiTokenService, FakeApiToken>();
        b.Services.AddSingleton<McpServer.Services.ApiServerClient>();
        b.Services.AddMcpServer().WithHttpTransport().AddM365Tools();

        var app = b.Build();
        app.MapMcp("/mcp");
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcp is not null) await _mcp.StopAsync();
        if (_api is not null) await _api.StopAsync();
    }

    private sealed class GraphStub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeGraphToken(IConfiguration c, IOptions<GraphAuthOptions> o) : GraphTokenService(c, o)
    {
        public override Task<string> GetGraphTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("graph-test-token");
    }

    private sealed class FakeApiToken(IOptions<AzureAdOptions> o) : ApiTokenService(o)
    {
        public override Task<string> GetAppOnlyTokenAsync() => Task.FromResult("api-test-token");
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("roles", ApiAuthorizationPolicies.AppRole), new Claim(ClaimTypes.NameIdentifier, "e2e")], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
