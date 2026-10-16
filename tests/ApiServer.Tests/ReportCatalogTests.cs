using System.Net;
using ApiServer.Auth;
using ApiServer.Services;
using McpServer.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiServer.Tests;

/// <summary>Katalog-Regeln: Copilot-Perioden, getApiUsage (nur delegiert) – siehe ADR-0008.</summary>
public class ReportCatalogTests
{
    private sealed class ThrowingFactory : IHttpClientFactory
    {
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Calls++;
            throw new InvalidOperationException("Es darf kein HTTP-Client angefordert werden.");
        }
    }

    private sealed class NoTokenService() : GraphTokenService(
        new ConfigurationBuilder().Build(),
        Options.Create(new GraphAuthOptions { UseManagedIdentity = true }))
    {
        public override Task<string> GetGraphTokenAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Es darf kein Graph-Token angefordert werden.");
    }

    [Fact]
    public void GetApiUsage_is_marked_delegated_only_and_unavailable_in_description()
    {
        var def = ReportCatalog.Reports["getApiUsage"];

        Assert.True(def.DelegatedOnly);
        Assert.Contains("NICHT VERFUEGBAR", def.Description);
    }

    [Fact]
    public void Only_getApiUsage_is_delegated_only()
    {
        Assert.Equal(["getApiUsage"], ReportCatalog.Reports.Values.Where(r => r.DelegatedOnly).Select(r => r.FunctionName));
    }

    [Fact]
    public void GetApiUsage_periods_follow_microsoft_docs()
    {
        var periods = ReportCatalog.GetAllowedPeriods(ReportCatalog.Reports["getApiUsage"]);

        Assert.Equal(["D7", "D30", "D60"], periods);
    }

    [Fact]
    public void Copilot_reports_reject_D30_and_default_to_D28()
    {
        var def = ReportCatalog.Reports["getMicrosoft365CopilotUserCountSummary"];

        Assert.DoesNotContain("D30", ReportCatalog.GetAllowedPeriods(def));
        Assert.Equal("D28", ReportCatalog.GetDefaultPeriod(def));
    }

    [Fact]
    public async Task GetApiUsage_is_rejected_without_any_graph_call()
    {
        var factory = new ThrowingFactory();
        var client = new GraphApiClient(factory, new NoTokenService(), NullLogger<GraphApiClient>.Instance);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.GetCatalogReportAsJsonAsync("getApiUsage", period: "D7"));

        Assert.Contains("keine Application-Permissions", ex.Message);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task Other_reports_are_not_blocked_by_delegated_only_rule()
    {
        var factory = new ThrowingFactory();
        var client = new GraphApiClient(factory, new NoTokenService(), NullLogger<GraphApiClient>.Instance);

        // Erreicht den Token-Abruf (der hier bewusst fehlschlaegt) -> wurde also nicht vorher abgewiesen.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogReportAsJsonAsync("getOffice365ActiveUserDetail", period: "D7"));
    }
}
