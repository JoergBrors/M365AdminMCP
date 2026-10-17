using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ApiServer.Auth;
using ApiServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiServer.Tests;

/// <summary>
/// Regressionstests fuer GraphApiClient.GetReportAsJsonAsync mit simulierten Graph- und Download-Antworten
/// (kein Netzwerk). Siehe ADR-0010.
/// </summary>
public class GraphApiClientReportTests
{
    private const string ReportPath = "/reports/getOffice365ActiveUserCounts(period='D7')";
    private const string SecretQuery = "token=SUPERSECRETPRESIGNEDTOKEN";
    private static readonly Uri DownloadUri = new($"https://reportsweu.office.com/data/beta/download?{SecretQuery}");

    private const string Csv = "Report Refresh Date,Report Period\r\n2026-10-14,7\r\n";

    // ---------- Hilfsklassen ----------

    private sealed class StubHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await respond(request, Requests.Count);
        }
    }

    private sealed class FakeFactory(HttpMessageHandler graph, HttpMessageHandler download) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(name == "GraphNoRedirect" ? graph : download, disposeHandler: false);
    }

    private sealed class FakeTokenService() : GraphTokenService(
        new ConfigurationBuilder().Build(),
        Options.Create(new GraphAuthOptions { UseManagedIdentity = true }))
    {
        public override Task<string> GetGraphTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("test-graph-token");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add($"{logLevel}: {formatter(state, exception)} {exception?.Message}");
    }

    private static readonly IReadOnlyList<TimeSpan> NoDelay = [TimeSpan.Zero, TimeSpan.Zero];

    private static HttpResponseMessage Json(string body = "{\"value\":[]}") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect() =>
        new(HttpStatusCode.Found) { Headers = { Location = DownloadUri } };

    private static HttpResponseMessage CsvResponse() =>
        new(HttpStatusCode.OK) { Content = new StringContent(Csv, System.Text.Encoding.UTF8, "text/csv") };

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body) };

    private static Exception ConnectionReset() => new HttpRequestException(
        "An error occurred while sending the request.",
        new IOException("Unable to read data from the transport connection", new SocketException((int)SocketError.ConnectionReset)));

    private static (GraphApiClient Client, StubHandler Graph, StubHandler Download, ListLogger<GraphApiClient> Log) Create(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> graph,
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? download = null)
    {
        var graphHandler = new StubHandler(graph);
        var downloadHandler = new StubHandler(download ?? ((_, _) => throw new InvalidOperationException("Kein Download erwartet.")));
        var log = new ListLogger<GraphApiClient>();
        var client = new GraphApiClient(new FakeFactory(graphHandler, downloadHandler), new FakeTokenService(), log, NoDelay);
        return (client, graphHandler, downloadHandler, log);
    }

    // ---------- JSON / Redirect ----------

    [Fact]
    public async Task Json_response_is_returned_without_download()
    {
        var (client, graph, download, _) = Create((_, _) => Task.FromResult(Json("{\"value\":[1]}")));

        var result = await client.GetReportAsJsonAsync(ReportPath);

        Assert.Equal("{\"value\":[1]}", result);
        Assert.Single(graph.Requests);
        Assert.Empty(download.Requests);
        Assert.Equal("Bearer", graph.Requests[0].Headers.Authorization?.Scheme);
        Assert.Contains("$format=application/json", graph.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Redirect_downloads_csv_without_authorization_header_and_converts_to_json()
    {
        var (client, graph, download, _) = Create(
            (_, _) => Task.FromResult(Redirect()),
            (_, _) => Task.FromResult(CsvResponse()));

        var result = await client.GetReportAsJsonAsync(ReportPath);

        Assert.Contains("\"Report Refresh Date\": \"2026-10-14\"", result);
        Assert.Single(download.Requests);
        Assert.Null(download.Requests[0].Headers.Authorization); // Graph-Token darf nie an den Download-Host
        Assert.Equal("reportsweu.office.com", download.Requests[0].RequestUri!.Host);
        Assert.NotNull(graph.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task Bad_request_for_json_falls_back_to_plain_request_with_csv_redirect()
    {
        var (client, graph, _, _) = Create(
            (request, _) => Task.FromResult(request.RequestUri!.Query.Contains("$format") ? Status(HttpStatusCode.BadRequest, "{\"error\":{}}") : Redirect()),
            (_, _) => Task.FromResult(CsvResponse()));

        var result = await client.GetReportAsJsonAsync(ReportPath);

        Assert.Contains("2026-10-14", result);
        Assert.Equal(2, graph.Requests.Count);
        Assert.DoesNotContain("$format", graph.Requests[1].RequestUri!.ToString());
    }

    // ---------- Verbindungsabbruch / transiente Fehler (Download) ----------

    [Fact]
    public async Task Download_connection_reset_is_retried_with_bound_and_then_fails_with_clear_message()
    {
        var (client, _, download, log) = Create(
            (_, _) => Task.FromResult(Redirect()),
            (_, _) => throw ConnectionReset());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetReportAsJsonAsync(ReportPath));

        Assert.Equal(3, download.Requests.Count); // 1 Versuch + 2 Wiederholungen, nie mehr
        Assert.Contains("3 Versuch", ex.Message);
        Assert.Contains("reportsweu.office.com", ex.Message);
        Assert.Contains("ohne Antwort", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(3, log.Messages.Count(m => m.StartsWith("Warning") || m.StartsWith("Error")));
    }

    [Fact]
    public async Task Download_reset_once_then_success_returns_data()
    {
        var (client, _, download, _) = Create(
            (_, _) => Task.FromResult(Redirect()),
            (_, attempt) => attempt == 1 ? throw ConnectionReset() : Task.FromResult(CsvResponse()));

        var result = await client.GetReportAsJsonAsync(ReportPath);

        Assert.Contains("2026-10-14", result);
        Assert.Equal(2, download.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Download_transient_status_is_retried(HttpStatusCode status)
    {
        var (client, _, download, _) = Create(
            (_, _) => Task.FromResult(Redirect()),
            (_, attempt) => Task.FromResult(attempt < 3 ? Status(status) : CsvResponse()));

        var result = await client.GetReportAsJsonAsync(ReportPath);

        Assert.Contains("2026-10-14", result);
        Assert.Equal(3, download.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Download_permanent_status_is_not_retried(HttpStatusCode status)
    {
        var (client, _, download, _) = Create(
            (_, _) => Task.FromResult(Redirect()),
            (_, _) => Task.FromResult(Status(status)));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetReportAsJsonAsync(ReportPath));

        Assert.Single(download.Requests);
    }

    // ---------- Graph-Aufruf ----------

    [Fact]
    public async Task Graph_connection_reset_twice_then_success_returns_data()
    {
        var (client, graph, _, _) = Create((_, attempt) => attempt <= 2 ? throw ConnectionReset() : Task.FromResult(Json()));

        var result = await client.GetReportAsJsonAsync(ReportPath);

        Assert.Equal("{\"value\":[]}", result);
        Assert.Equal(3, graph.Requests.Count);
    }

    [Fact]
    public async Task Graph_503_is_retried_but_401_is_not()
    {
        var (retrying, graph503, _, _) = Create((_, attempt) => Task.FromResult(attempt == 1 ? Status(HttpStatusCode.ServiceUnavailable) : Json()));
        await retrying.GetReportAsJsonAsync(ReportPath);
        Assert.Equal(2, graph503.Requests.Count);

        var (failing, graph401, _, _) = Create((_, _) => Task.FromResult(Status(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"Authentication_Unauthorized\"}}")));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => failing.GetReportAsJsonAsync(ReportPath));
        Assert.Single(graph401.Requests);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("Authentication_Unauthorized", ex.Message);
    }

    [Fact]
    public async Task Graph_error_after_retries_is_bounded()
    {
        var (client, graph, _, _) = Create((_, _) => Task.FromResult(Status(HttpStatusCode.ServiceUnavailable)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetReportAsJsonAsync(ReportPath));

        Assert.Equal(3, graph.Requests.Count);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        var (client, graph, _, _) = Create((_, _) =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetReportAsJsonAsync(ReportPath, cts.Token));

        Assert.Single(graph.Requests);
    }

    // ---------- Logging ----------

    [Fact]
    public async Task Logs_never_contain_presigned_url_query_or_tokens()
    {
        var (client, _, _, log) = Create(
            (_, _) => Task.FromResult(Redirect()),
            (_, _) => throw ConnectionReset());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetReportAsJsonAsync(ReportPath));

        Assert.NotEmpty(log.Messages);
        Assert.All(log.Messages, message =>
        {
            Assert.DoesNotContain("SUPERSECRETPRESIGNEDTOKEN", message);
            Assert.DoesNotContain("test-graph-token", message);
            Assert.DoesNotContain("token=", message);
            Assert.Contains("reportsweu.office.com", message);
            Assert.Contains("ConnectionReset", message); // SocketError wird zur Diagnose mitgeloggt
        });
    }

    [Fact]
    public async Task Exception_message_does_not_contain_presigned_url_query()
    {
        var (client, _, _, _) = Create(
            (_, _) => Task.FromResult(Redirect()),
            (_, _) => throw ConnectionReset());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetReportAsJsonAsync(ReportPath));

        Assert.DoesNotContain("SUPERSECRETPRESIGNEDTOKEN", ex.Message);
    }

    // ---------- Parametervalidierung (ADR-0008) ----------

    [Fact]
    public async Task Catalog_report_rejects_injected_date_before_any_graph_call()
    {
        var (client, graph, _, _) = Create((_, _) => Task.FromResult(Json()));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetCatalogReportAsJsonAsync("getOffice365ActiveUserDetail", date: "2026-10-14)/../../users"));

        Assert.Empty(graph.Requests);
    }

    [Fact]
    public async Task Catalog_report_accepts_valid_date_and_puts_it_into_the_path()
    {
        var (client, graph, _, _) = Create((_, _) => Task.FromResult(Json()));

        await client.GetCatalogReportAsJsonAsync("getOffice365ActiveUserDetail", date: "2026-10-14");

        Assert.Contains("date=2026-10-14", Uri.UnescapeDataString(graph.Requests[0].RequestUri!.ToString()));
    }
}
