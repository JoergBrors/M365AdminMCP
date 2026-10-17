using System.Net;
using McpServer.Hardening;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiServer.Tests;

/// <summary>Tests fuer Rate Limiting, Request-Groesse und Client-IP-Ermittlung (Findings F-009, D-5).</summary>
public class RequestHardeningTests
{
    private static DefaultHttpContext Context(string? forwardedFor, string? remote = "10.0.0.1")
    {
        var ctx = new DefaultHttpContext();
        if (forwardedFor is not null) ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (remote is not null) ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        return ctx;
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("203.0.113.9:4711", "203.0.113.9")]
    [InlineData("6.6.6.6, 203.0.113.9:4711", "203.0.113.9")]   // gefaelschter erster Eintrag wird ignoriert
    [InlineData("6.6.6.6, 7.7.7.7, 203.0.113.9", "203.0.113.9")]
    public void Client_ip_is_the_last_forwarded_entry(string header, string expected) =>
        Assert.Equal(expected, RequestHardening.GetClientIp(Context(header)));

    [Fact]
    public void Without_forwarded_header_the_remote_address_is_used() =>
        Assert.Equal("10.0.0.1", RequestHardening.GetClientIp(Context(null)));

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Dictionary<string, string?> config)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(config);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRequestHardening(builder.Configuration);
        builder.WebHost.ConfigureRequestLimits(builder.Configuration);

        var app = builder.Build();
        app.UseRequestHardening();
        app.MapGet("/ping", () => "ok");
        app.MapPost("/token", async (HttpRequest r) =>
        {
            using var reader = new StreamReader(r.Body);
            return (await reader.ReadToEndAsync()).Length;
        });
        app.MapGet("/healthz", () => "healthy");
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client);
    }

    [Fact]
    public async Task General_limit_returns_429_with_retry_after_but_healthz_is_exempt()
    {
        var (app, client) = await StartAsync(new() { ["Hardening:PermitLimitPerMinute"] = "3" });
        try
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ping")).StatusCode);
            }

            var limited = await client.GetAsync("/ping");
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.Contains("Retry-After"));

            for (var i = 0; i < 10; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
            }
        }
        finally { await app.StopAsync(); client.Dispose(); }
    }

    [Fact]
    public async Task Forged_first_forwarded_entry_does_not_evade_the_limit()
    {
        var (app, client) = await StartAsync(new() { ["Hardening:PermitLimitPerMinute"] = "2" });
        try
        {
            for (var i = 0; i < 2; i++)
            {
                using var ok = new HttpRequestMessage(HttpMethod.Get, "/ping");
                ok.Headers.Add("X-Forwarded-For", $"9.9.9.{i}, 203.0.113.9");   // Angreifer rotiert den ersten Eintrag
                Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ok)).StatusCode);
            }

            using var third = new HttpRequestMessage(HttpMethod.Get, "/ping");
            third.Headers.Add("X-Forwarded-For", "9.9.9.99, 203.0.113.9");
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(third)).StatusCode);
        }
        finally { await app.StopAsync(); client.Dispose(); }
    }

    [Fact]
    public async Task OAuth_paths_have_a_stricter_limit()
    {
        var (app, client) = await StartAsync(new()
        {
            ["Hardening:PermitLimitPerMinute"] = "100",
            ["Hardening:OAuthPermitLimitPerMinute"] = "2"
        });
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/token", new StringContent("a"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/token", new StringContent("a"))).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/token", new StringContent("a"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ping")).StatusCode);   // allgemeiner Pfad unberuehrt
        }
        finally { await app.StopAsync(); client.Dispose(); }
    }

    [Fact]
    public async Task Oversized_request_body_is_rejected()
    {
        var (app, client) = await StartAsync(new()
        {
            ["Hardening:MaxRequestBodyBytes"] = "100",
            ["Hardening:OAuthPermitLimitPerMinute"] = "100"
        });
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/token", new StringContent(new string('a', 50)))).StatusCode);
            var big = await client.PostAsync("/token", new StringContent(new string('a', 5000)));
            Assert.True(big.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
                $"erwartet 413/400, erhalten {(int)big.StatusCode}");
        }
        finally { await app.StopAsync(); client.Dispose(); }
    }

    [Fact]
    public async Task Disabled_hardening_applies_no_limits()
    {
        var (app, client) = await StartAsync(new()
        {
            ["Hardening:Enabled"] = "false",
            ["Hardening:PermitLimitPerMinute"] = "1"
        });
        try
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ping")).StatusCode);
            }
        }
        finally { await app.StopAsync(); client.Dispose(); }
    }
}
