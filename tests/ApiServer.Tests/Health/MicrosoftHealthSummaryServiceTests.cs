using System.Net;
using ApiServer.Health;

namespace ApiServer.Tests.Health;

/// <summary>Aggregation: Teilfehler duerfen andere Quellen nicht beeintraechtigen; keine globale Gesundheitsbewertung.</summary>
public class MicrosoftHealthSummaryServiceTests
{
    private static Task<HttpResponseMessage> Respond(HttpRequestMessage request, bool azureFails, bool graphFails)
    {
        var host = request.RequestUri!.Host;
        var path = request.RequestUri.AbsolutePath;

        if (host == "management.azure.com")
        {
            return azureFails
                ? T.Task_(T.Error(HttpStatusCode.Forbidden, "AuthorizationFailed", "kein Zugriff"))
                : T.Task_(T.Page("", nextLinkProperty: "nextLink"));
        }

        if (graphFails && path.EndsWith("/issues"))
        {
            return T.Task_(T.Error(HttpStatusCode.Forbidden, "Forbidden", "kein Zugriff"));
        }

        return T.Task_(path switch
        {
            var p when p.EndsWith("/healthOverviews") => T.Page("{\"id\":\"Intune\",\"service\":\"Microsoft Intune\",\"status\":\"serviceOperational\"}"),
            var p when p.EndsWith("/issues") => T.Page("{\"id\":\"IT1\",\"service\":\"Microsoft Intune\",\"isResolved\":false,\"lastModifiedDateTime\":\"2026-10-15T00:00:00Z\"}"),
            _ => T.Page("{\"id\":\"MC1\",\"services\":[\"Microsoft Intune\"],\"lastModifiedDateTime\":\"2026-10-10T00:00:00Z\"}")
        });
    }

    private static MicrosoftHealthSummaryService Create(bool azureFails, bool graphFails, Action<MicrosoftHealthOptions>? configure = null, StubHandler? captured = null)
    {
        var options = T.Options(configure);
        var handler = new StubHandler((request, _) => Respond(request, azureFails, graphFails));
        var runner = new HealthRequestRunner(new SingleHandlerFactory(handler), T.Opt(options), new ListLogger<HealthRequestRunner>(),
            [TimeSpan.Zero], (_, _) => Task.CompletedTask);
        var time = new FixedTime(T.Now);
        var graph = new MicrosoftServiceHealthClient(runner, new FakeGraphToken(), T.Opt(options), time);
        var azure = new AzureResourceHealthClient(runner, new FakeArmToken(), T.Opt(options), time);
        return new MicrosoftHealthSummaryService(graph, azure, T.Opt(options), time);
    }

    [Fact]
    public async Task All_sources_ok_gives_ok_with_one_status_entry_per_source()
    {
        var summary = await Create(false, false).GetSummaryAsync();

        Assert.Equal("ok", summary.Status);
        Assert.False(summary.IsPartial);
        Assert.Equal(["intune_service_health", "intune_active_incidents", "intune_messages", $"azure_service_health:{T.SubA}"],
            summary.Sources.Select(s => s.Name));
        Assert.All(summary.Sources, s => Assert.Equal(T.Now.UtcDateTime, s.QueriedAtUtc));
        Assert.Equal("Intune", Assert.Single(summary.IntuneServiceHealth.Items).Id);
        Assert.Equal("IT1", Assert.Single(summary.ActiveIntuneIncidents.Items).Id);
        Assert.Equal("MC1", Assert.Single(summary.IntuneMessages.Items).Id);
    }

    [Fact]
    public async Task Azure_failure_is_isolated_and_other_sources_still_deliver()
    {
        var summary = await Create(azureFails: true, graphFails: false).GetSummaryAsync();

        Assert.Equal("partial", summary.Status);
        Assert.True(summary.IsPartial);
        Assert.Equal("ok", summary.IntuneServiceHealth.Status);
        Assert.Equal(1, summary.IntuneServiceHealth.ItemCount);
        var azure = Assert.Single(summary.AzureServiceHealth);
        Assert.Equal("error", azure.Status);
        Assert.Equal("forbidden", azure.Errors[0].Code);
        Assert.Contains(summary.Sources, s => s.Name.StartsWith("azure_service_health") && s.Status == "error");
    }

    [Fact]
    public async Task Graph_partial_failure_keeps_azure_and_other_graph_calls()
    {
        var summary = await Create(azureFails: false, graphFails: true).GetSummaryAsync();

        Assert.Equal("partial", summary.Status);
        Assert.Equal("error", summary.ActiveIntuneIncidents.Status);
        Assert.Equal("ok", summary.IntuneServiceHealth.Status);
        Assert.Equal("ok", summary.IntuneMessages.Status);
    }

    [Fact]
    public async Task Azure_without_allowed_subscriptions_is_disabled_but_graph_works()
    {
        var summary = await Create(false, false, o => o.AllowedSubscriptionIds = []).GetSummaryAsync();

        Assert.Equal("ok", summary.Status); // deaktivierte Quellen zaehlen nicht als Fehler
        var azure = Assert.Single(summary.AzureServiceHealth);
        Assert.Equal("disabled", azure.Status);
        Assert.Contains(summary.Sources, s => s.Status == "disabled");
    }

    [Fact]
    public async Task Everything_disabled_gives_disabled_status()
    {
        var summary = await Create(false, false, o => o.Enabled = false).GetSummaryAsync();

        Assert.Equal("disabled", summary.Status);
        Assert.All(summary.Sources, s => Assert.Equal("disabled", s.Status));
    }

    [Fact]
    public async Task Every_source_failing_gives_error_not_a_health_verdict()
    {
        var options = T.Options();
        var handler = new StubHandler((_, _) => T.Task_(T.Error(HttpStatusCode.Forbidden, "Forbidden", "kein Zugriff")));
        var runner = new HealthRequestRunner(new SingleHandlerFactory(handler), T.Opt(options), new ListLogger<HealthRequestRunner>(),
            [TimeSpan.Zero], (_, _) => Task.CompletedTask);
        var time = new FixedTime(T.Now);
        var service = new MicrosoftHealthSummaryService(
            new MicrosoftServiceHealthClient(runner, new FakeGraphToken(), T.Opt(options), time),
            new AzureResourceHealthClient(runner, new FakeArmToken(), T.Opt(options), time), T.Opt(options), time);

        var summary = await service.GetSummaryAsync();

        Assert.Equal("error", summary.Status);
        Assert.All(summary.Sources, s => Assert.Equal("error", s.Status));
    }
}
