using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ApiServer.Auth;
using ApiServer.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiServer.Tests.Health;

internal sealed class StubHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Authorizations { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
        return await respond(request, Requests.Count).WaitAsync(cancellationToken);
    }
}

internal sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public string? LastName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        LastName = name;
        return new HttpClient(handler, disposeHandler: false);
    }
}

internal sealed class FakeGraphToken() : GraphTokenService(
    new ConfigurationBuilder().Build(), Options.Create(new GraphAuthOptions { UseManagedIdentity = true }))
{
    public override Task<string> GetGraphTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("graph-test-token");
}

internal sealed class FakeArmToken() : ArmTokenService(
    new ConfigurationBuilder().Build(), Options.Create(new GraphAuthOptions { UseManagedIdentity = true }))
{
    public override Task<string> GetArmTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("arm-test-token");
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add($"{logLevel}: {formatter(state, exception)}");
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class T
{
    public const string SubA = "11111111-1111-1111-1111-111111111111";
    public const string SubB = "22222222-2222-2222-2222-222222222222";
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public static MicrosoftHealthOptions Options(Action<MicrosoftHealthOptions>? configure = null)
    {
        var options = new MicrosoftHealthOptions { AllowedSubscriptionIds = [SubA], RequestTimeoutSeconds = 5 };
        configure?.Invoke(options);
        return options;
    }

    public static IOptions<MicrosoftHealthOptions> Opt(MicrosoftHealthOptions options) => Microsoft.Extensions.Options.Options.Create(options);

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode code, string errorCode, string message) =>
        Json($"{{\"error\":{{\"code\":\"{errorCode}\",\"message\":\"{message}\"}}}}", code);

    public static HttpResponseMessage Throttled(TimeSpan retryAfter)
    {
        var response = Status(HttpStatusCode.TooManyRequests, "{}");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    /// <summary>Eine Collection-Seite; nextLinkProperty ist "@odata.nextLink" (Graph) oder "nextLink" (ARM).</summary>
    public static HttpResponseMessage Page(string valuesJson, string? nextLink = null, string nextLinkProperty = "@odata.nextLink") =>
        Json(nextLink is null ? $"{{\"value\":[{valuesJson}]}}" : $"{{\"value\":[{valuesJson}],\"{nextLinkProperty}\":\"{nextLink}\"}}");

    public static Exception ConnectionReset() => new HttpRequestException(
        "An error occurred while sending the request.",
        new IOException("transport", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)));

    public sealed class Harness
    {
        public required StubHandler Handler { get; init; }
        public required HealthRequestRunner Runner { get; init; }
        public required MicrosoftHealthOptions Options { get; init; }
        public required List<TimeSpan> Delays { get; init; }
        public required ListLogger<HealthRequestRunner> Log { get; init; }
        public required SingleHandlerFactory Factory { get; init; }
    }

    public static Harness Create(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond,
        Action<MicrosoftHealthOptions>? configure = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        var options = Options(configure);
        var handler = new StubHandler(respond);
        var factory = new SingleHandlerFactory(handler);
        var delays = new List<TimeSpan>();
        var log = new ListLogger<HealthRequestRunner>();
        var runner = new HealthRequestRunner(factory, Opt(options), log,
            retryDelays ?? [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)],
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        return new Harness { Handler = handler, Runner = runner, Options = options, Delays = delays, Log = log, Factory = factory };
    }

    public static (MicrosoftServiceHealthClient Client, Harness H) GraphClient(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond, Action<MicrosoftHealthOptions>? configure = null)
    {
        var h = Create(respond, configure);
        return (new MicrosoftServiceHealthClient(h.Runner, new FakeGraphToken(), Opt(h.Options), new FixedTime(Now)), h);
    }

    public static (AzureResourceHealthClient Client, Harness H) AzureClient(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond, Action<MicrosoftHealthOptions>? configure = null)
    {
        var h = Create(respond, configure);
        return (new AzureResourceHealthClient(h.Runner, new FakeArmToken(), Opt(h.Options), new FixedTime(Now)), h);
    }

    public static Task<HttpResponseMessage> Task_(HttpResponseMessage response) => Task.FromResult(response);
}
