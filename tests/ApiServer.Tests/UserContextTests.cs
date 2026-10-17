using System.Security.Claims;
using ApiServer.Auth;
using McpServer.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApiServer.Tests;

/// <summary>UserContext (nur Audit, D-1/D-2): Aufbau aus validierten Claims, strikte Validierung beim Lesen, keine Autorisierungswirkung.</summary>
public class UserContextTests
{
    private const string Oid = "75dcc81d-5cf4-406e-98c1-0a986b4c04ee";
    private const string Tid = "db88f9b6-c89a-41a3-a21d-b61458e234ff";

    private static ClaimsPrincipal Entra(params Claim[] claims) => new(new ClaimsIdentity(claims, "AuthenticationTypes.Federation"));

    [Fact]
    public void Entra_principal_yields_oid_tid_and_username()
    {
        var ctx = UserContext.FromPrincipal(Entra(new("oid", Oid), new("tid", Tid), new("preferred_username", "joerg@brors.net")));
        Assert.Equal(new UserContext("entra", Oid, Tid, "joerg@brors.net"), ctx);
    }

    [Fact]
    public void Long_claim_type_names_are_accepted()
    {
        var ctx = UserContext.FromPrincipal(Entra(
            new("http://schemas.microsoft.com/identity/claims/objectidentifier", Oid),
            new("http://schemas.microsoft.com/identity/claims/tenantid", Tid)));
        Assert.Equal((Oid, Tid), (ctx.Oid, ctx.Tid));
    }

    [Fact]
    public void Api_key_principal_has_no_user_identity()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "mcp-api-key-client"), new Claim(ClaimTypes.Role, "Mcp.Access")], "McpApiKey"));
        var ctx = UserContext.FromPrincipal(principal);
        Assert.Equal(new UserContext("apikey", null, null, null), ctx);
    }

    [Fact]
    public void Unauthenticated_or_null_principal_is_anonymous()
    {
        Assert.Equal(UserContext.Anonymous, UserContext.FromPrincipal(null));
        Assert.Equal(UserContext.Anonymous, UserContext.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void Round_trip_through_headers_preserves_valid_values()
    {
        var original = new UserContext("entra", Oid, Tid, "joerg@brors.net");
        var headers = new HeaderDictionary();
        foreach (var (name, value) in original.ToHeaders()) headers[name] = value;
        Assert.Equal(original, UserContext.FromHeaders(headers));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("75dcc81d-5cf4-406e-98c1-0a986b4c04ee\r\nX-Evil: 1")]
    [InlineData("{75dcc81d-5cf4-406e-98c1-0a986b4c04ee}")]
    public void Invalid_guids_are_dropped(string bad)
    {
        var headers = new HeaderDictionary
        {
            [UserContext.MethodHeader] = "entra", [UserContext.OidHeader] = bad, [UserContext.TidHeader] = bad
        };
        var ctx = UserContext.FromHeaders(headers);
        Assert.Null(ctx.Oid);
        Assert.Null(ctx.Tid);
    }

    [Theory]
    [InlineData("a b@x.de")]
    [InlineData("user@x.de\nSet-Cookie: x")]
    [InlineData("<script>@x.de")]
    public void Unsafe_usernames_are_dropped(string bad)
    {
        var headers = new HeaderDictionary { [UserContext.MethodHeader] = "entra", [UserContext.UpnHeader] = bad };
        Assert.Null(UserContext.FromHeaders(headers).PreferredUsername);
    }

    [Fact]
    public void Overlong_username_is_dropped()
    {
        var headers = new HeaderDictionary { [UserContext.MethodHeader] = "entra", [UserContext.UpnHeader] = new string('a', 300) + "@x.de" };
        Assert.Null(UserContext.FromHeaders(headers).PreferredUsername);
    }

    [Theory]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("none")]
    public void Unknown_or_missing_method_means_anonymous(string method)
    {
        var headers = new HeaderDictionary { [UserContext.MethodHeader] = method, [UserContext.OidHeader] = Oid };
        Assert.Equal(UserContext.Anonymous, UserContext.FromHeaders(headers));
    }

    // ---------- Middleware ----------

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages, categoryName);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "McpUserAudit") lock (sink) sink.Add(formatter(state, exception));
            }
        }
    }

    private static async Task<(WebApplication App, HttpClient Client, CapturingProvider Log)> StartAsync(bool includeUpn = true)
    {
        var provider = new CapturingProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:LogUserPrincipalName"] = includeUpn.ToString() });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(provider);
        var app = builder.Build();
        app.UseUserContextAudit();
        app.MapGet("/data", () => "ok");
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, provider);
    }

    [Fact]
    public async Task Middleware_logs_validated_context_and_never_blocks_the_request()
    {
        var (app, client, log) = await StartAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/data");
            request.Headers.Add(UserContext.MethodHeader, "entra");
            request.Headers.Add(UserContext.OidHeader, Oid);
            request.Headers.Add(UserContext.TidHeader, Tid);
            request.Headers.Add(UserContext.UpnHeader, "joerg@brors.net");
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
            Assert.Contains(log.Messages, m => m.Contains(Oid) && m.Contains("joerg@brors.net") && m.Contains("authMethod=entra"));
        }
        finally { await app.StopAsync(); client.Dispose(); }
    }

    [Fact]
    public async Task Middleware_ignores_forged_values_and_requests_without_header_stay_unaffected()
    {
        var (app, client, log) = await StartAsync();
        try
        {
            using var forged = new HttpRequestMessage(HttpMethod.Get, "/data");
            forged.Headers.Add(UserContext.MethodHeader, "entra");
            forged.Headers.Add(UserContext.OidHeader, "'; DROP TABLE users;--");
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.SendAsync(forged)).StatusCode);
            Assert.DoesNotContain(log.Messages, m => m.Contains("DROP TABLE"));

            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/data")).StatusCode);   // ohne Header: kein Eintrag, keine Wirkung
            Assert.Single(log.Messages);
        }
        finally { await app.StopAsync(); client.Dispose(); }
    }

    [Fact]
    public async Task Username_can_be_left_out_of_the_log_for_privacy()
    {
        var (app, client, log) = await StartAsync(includeUpn: false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/data");
            request.Headers.Add(UserContext.MethodHeader, "entra");
            request.Headers.Add(UserContext.OidHeader, Oid);
            request.Headers.Add(UserContext.UpnHeader, "joerg@brors.net");
            await client.SendAsync(request);
            Assert.DoesNotContain(log.Messages, m => m.Contains("joerg@brors.net"));
            Assert.Contains(log.Messages, m => m.Contains(Oid));
        }
        finally { await app.StopAsync(); client.Dispose(); }
    }
}
