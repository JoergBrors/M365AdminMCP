using System.Net;

namespace ApiServer.Tests.Health;

/// <summary>Microsoft Graph Service Communications (Intune Health, Incidents, Message Center) mit simulierten Antworten.</summary>
public class MicrosoftServiceHealthClientTests
{
    private const string Overviews =
        "{\"id\":\"Exchange\",\"service\":\"Exchange Online\",\"status\":\"serviceOperational\"}," +
        "{\"id\":\"Intune\",\"service\":\"Microsoft Intune\",\"status\":\"serviceDegradation\"}," +
        "{\"id\":\"MobileDeviceManagement\",\"service\":\"Basic Mobility and Security\",\"status\":\"serviceOperational\"}";

    private static string Issue(string id, string service, bool resolved, string modified, string status = "investigating") =>
        $"{{\"id\":\"{id}\",\"title\":\"Titel {id}\",\"service\":\"{service}\",\"status\":\"{status}\",\"classification\":\"incident\"," +
        $"\"origin\":\"microsoft\",\"isResolved\":{(resolved ? "true" : "false")},\"startDateTime\":\"2026-10-01T00:00:00Z\"," +
        $"\"lastModifiedDateTime\":\"{modified}\",\"feature\":\"Enrollment\",\"featureGroup\":\"Devices\",\"impactDescription\":\"Impact {id}\"}}";

    private static string Message(string id, string[] services, string modified, string? actionBy = null) =>
        $"{{\"id\":\"{id}\",\"title\":\"Titel {id}\",\"services\":[{string.Join(",", services.Select(s => $"\"{s}\""))}]," +
        $"\"category\":\"planForChange\",\"severity\":\"normal\",\"startDateTime\":\"2026-09-01T00:00:00Z\"," +
        $"\"lastModifiedDateTime\":\"{modified}\",\"isMajorChange\":false,\"actionRequiredByDateTime\":{(actionBy is null ? "null" : $"\"{actionBy}\"")}," +
        "\"tags\":[\"Admin impact\"],\"body\":{\"contentType\":\"html\",\"content\":\"<p>Hallo <b>Welt</b></p>\"}}";

    // ---------- Intune Service Health ----------

    [Fact]
    public async Task Intune_health_is_identified_by_exact_service_and_uses_graph_v1_with_bearer()
    {
        var (client, h) = T.GraphClient((_, _) => T.Task_(T.Page(Overviews)));

        var result = await client.GetIntuneHealthAsync();

        Assert.Equal("ok", result.Status);
        var item = Assert.Single(result.Items);
        Assert.Equal("Intune", item.Id);
        Assert.Equal("serviceDegradation", item.Status);
        Assert.False(item.IsOperational);
        Assert.Equal("https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/healthOverviews", h.Handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer graph-test-token", h.Handler.Authorizations[0]);
        Assert.Equal(T.Now.UtcDateTime, result.QueriedAtUtc);
    }

    [Fact]
    public async Task Intune_health_does_not_use_substring_matching_and_never_claims_health_when_service_missing()
    {
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page(
            "{\"id\":\"IntuneFake\",\"service\":\"Not Microsoft Intune Related\",\"status\":\"serviceOperational\"}," +
            "{\"id\":\"Exchange\",\"service\":\"Exchange Online\",\"status\":\"serviceOperational\"}")));

        var result = await client.GetIntuneHealthAsync();

        Assert.Equal("no_data", result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("intune_service_not_found", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Intune_health_empty_collection_is_no_data()
    {
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page("")));

        var result = await client.GetIntuneHealthAsync();

        Assert.Equal("no_data", result.Status);
        Assert.Equal(0, result.ItemCount);
    }

    // ---------- Incidents ----------

    [Fact]
    public async Task Incidents_return_only_intune_sorted_by_last_modification()
    {
        var page = string.Join(",",
            Issue("IT1", "Microsoft Intune", true, "2026-09-01T00:00:00Z", "serviceRestored"),
            Issue("EX1", "Exchange Online", false, "2026-10-10T00:00:00Z"),
            Issue("IT3", "Microsoft Intune", false, "2026-10-12T00:00:00Z"),
            Issue("IT2", "Microsoft Intune", true, "2026-10-05T00:00:00Z", "serviceRestored"));
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page(page)));

        var result = await client.GetIntuneIncidentsAsync(activeOnly: false, maxResults: null);

        Assert.Equal(["IT3", "IT2", "IT1"], result.Items.Select(i => i.Id));
        Assert.All(result.Items, i => Assert.Equal("Microsoft Intune", i.Service));
    }

    [Fact]
    public async Task Incidents_activeOnly_filters_resolved_ones()
    {
        var page = string.Join(",",
            Issue("IT1", "Microsoft Intune", true, "2026-09-01T00:00:00Z"),
            Issue("IT2", "Microsoft Intune", false, "2026-10-05T00:00:00Z"));
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page(page)));

        var active = await client.GetIntuneIncidentsAsync(activeOnly: true, maxResults: null);

        var item = Assert.Single(active.Items);
        Assert.Equal("IT2", item.Id);
        Assert.False(item.IsResolved);
        Assert.Equal("Impact IT2", item.ImpactDescription);
    }

    [Fact]
    public async Task Incidents_without_matches_are_no_data_not_healthy()
    {
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page(Issue("EX1", "Exchange Online", false, "2026-10-10T00:00:00Z"))));

        var result = await client.GetIntuneIncidentsAsync(true, null);

        Assert.Equal("no_data", result.Status);
    }

    [Fact]
    public async Task Incidents_maxResults_truncates_and_reports_it()
    {
        var page = string.Join(",", Enumerable.Range(1, 5).Select(i => Issue($"IT{i}", "Microsoft Intune", false, $"2026-10-0{i}T00:00:00Z")));
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page(page)));

        var result = await client.GetIntuneIncidentsAsync(false, 2);

        Assert.Equal(2, result.ItemCount);
        Assert.True(result.Truncated);
        Assert.True(result.IsPartial);
        Assert.NotNull(result.TruncationReason);
        Assert.Equal(["IT5", "IT4"], result.Items.Select(i => i.Id)); // neueste zuerst
    }

    [Fact]
    public async Task Incidents_follow_pagination_across_pages_and_only_graph_host()
    {
        var (client, h) = T.GraphClient((request, n) => T.Task_(n switch
        {
            1 => T.Page(Issue("A", "Microsoft Intune", false, "2026-10-01T00:00:00Z"), "https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues?$skiptoken=2"),
            2 => T.Page(Issue("B", "Microsoft Intune", false, "2026-10-02T00:00:00Z"), "https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues?$skiptoken=3"),
            _ => T.Page(Issue("C", "Microsoft Intune", false, "2026-10-03T00:00:00Z"))
        }));

        var result = await client.GetIntuneIncidentsAsync(false, null);

        Assert.Equal(["C", "B", "A"], result.Items.Select(i => i.Id));
        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.False(result.Truncated);
    }

    [Theory]
    [InlineData("https://evil.example/steal?x=1")]
    [InlineData("http://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues?$skiptoken=2")]
    [InlineData("https://graph.microsoft.com.evil.example/v1.0/admin/serviceAnnouncement/issues")]
    [InlineData("https://user@graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues")]
    public async Task Pagination_rejects_foreign_or_insecure_next_links_and_never_sends_token_there(string nextLink)
    {
        var (client, h) = T.GraphClient((_, _) => T.Task_(T.Page(Issue("A", "Microsoft Intune", false, "2026-10-01T00:00:00Z"), nextLink)));

        var result = await client.GetIntuneIncidentsAsync(false, null);

        Assert.Single(h.Handler.Requests); // zweite Seite wurde NICHT angefordert
        Assert.True(result.Truncated);
        Assert.Contains("nextLink", result.TruncationReason);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Pagination_is_bounded_by_MaxPages()
    {
        var (client, h) = T.GraphClient(
            (_, n) => T.Task_(T.Page(Issue($"I{n}", "Microsoft Intune", false, "2026-10-01T00:00:00Z"),
                "https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues?$skiptoken=" + n)),
            o => o.MaxPages = 3);

        var result = await client.GetIntuneIncidentsAsync(false, null);

        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.True(result.Truncated);
        Assert.Contains("MaxPages", result.TruncationReason);
    }

    // ---------- Message Center ----------

    [Fact]
    public async Task Messages_default_returns_only_intune_relevant()
    {
        var page = string.Join(",",
            Message("MC1", ["Microsoft Intune"], "2026-10-10T00:00:00Z"),
            Message("MC2", ["Exchange Online"], "2026-10-11T00:00:00Z"),
            Message("MC3", ["Microsoft Teams", "Microsoft Intune"], "2026-10-12T00:00:00Z", "2026-11-01T00:00:00Z"));
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page(page)));

        var result = await client.GetMessagesAsync(onlyIntune: true, null, null, null);

        Assert.Equal(["MC3", "MC1"], result.Items.Select(m => m.Id));
        var withAction = result.Items[0];
        Assert.True(withAction.ActionRequired);
        Assert.NotNull(withAction.ActionRequiredByDateTime);
        Assert.False(result.Items[1].ActionRequired);
        Assert.Equal("Hallo Welt", withAction.BodyPreview); // HTML entfernt
    }

    [Fact]
    public async Task Messages_without_filter_return_all_services_and_serviceName_filters_exactly()
    {
        var page = string.Join(",",
            Message("MC1", ["Microsoft Intune"], "2026-10-10T00:00:00Z"),
            Message("MC2", ["Exchange Online"], "2026-10-11T00:00:00Z"));
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page(page)));

        var all = await client.GetMessagesAsync(false, null, null, null);
        var exchange = await client.GetMessagesAsync(false, null, "exchange online", null);

        Assert.Equal(2, all.ItemCount);
        Assert.Equal("MC2", Assert.Single(exchange.Items).Id);
    }

    [Fact]
    public async Task Messages_sinceUtc_filters_by_last_modification()
    {
        var page = string.Join(",",
            Message("OLD", ["Microsoft Intune"], "2026-08-01T00:00:00Z"),
            Message("NEW", ["Microsoft Intune"], "2026-10-10T00:00:00Z"));
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page(page)));

        var result = await client.GetMessagesAsync(true, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null, null);

        Assert.Equal("NEW", Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task Messages_tolerate_missing_and_unexpected_fields()
    {
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Page(
            "{\"id\":\"MC9\",\"services\":[\"Microsoft Intune\"]}," +
            "{\"id\":\"MC8\",\"services\":\"kein-array\",\"lastModifiedDateTime\":12345,\"body\":\"text\"}")));

        var result = await client.GetMessagesAsync(true, null, null, null);

        var item = Assert.Single(result.Items);
        Assert.Equal("MC9", item.Id);
        Assert.Null(item.Title);
        Assert.Null(item.LastModifiedDateTime);
        Assert.Empty(item.Tags);
        Assert.Null(item.BodyPreview);
    }

    // ---------- Fehler ----------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    public async Task Http_errors_are_mapped_to_stable_codes_and_not_retried(HttpStatusCode status, string code)
    {
        var (client, h) = T.GraphClient((_, _) => T.Task_(T.Error(status, "Authorization_RequestDenied", "Insufficient privileges")));

        var result = await client.GetIntuneHealthAsync();

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.Equal((int)status, error.HttpStatus);
        Assert.Single(h.Handler.Requests);
        Assert.DoesNotContain("graph-test-token", error.Message);
    }

    [Fact]
    public async Task Forbidden_names_the_graph_permissions_to_check()
    {
        var (client, _) = T.GraphClient((_, _) => T.Task_(T.Error(HttpStatusCode.Forbidden, "Forbidden", "denied")));

        var result = await client.GetMessagesAsync(true, null, null, null);

        Assert.Contains("ServiceMessage.Read.All", result.Errors[0].Message);
        Assert.Contains("ServiceHealth.Read.All", result.Errors[0].Message);
    }

    [Fact]
    public async Task Throttling_429_honors_Retry_After_then_succeeds()
    {
        var (client, h) = T.GraphClient((_, n) => T.Task_(n == 1 ? T.Throttled(TimeSpan.FromSeconds(7)) : T.Page(Overviews)));

        var result = await client.GetIntuneHealthAsync();

        Assert.Equal("ok", result.Status);
        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], h.Delays);
    }

    [Fact]
    public async Task Retry_After_is_capped_at_30_seconds()
    {
        var (client, h) = T.GraphClient((_, n) => T.Task_(n == 1 ? T.Throttled(TimeSpan.FromMinutes(10)) : T.Page(Overviews)));

        await client.GetIntuneHealthAsync();

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(h.Delays));
    }

    [Fact]
    public async Task Persistent_503_is_retried_a_bounded_number_of_times_then_reported()
    {
        var (client, h) = T.GraphClient((_, _) => T.Task_(T.Status(HttpStatusCode.ServiceUnavailable)));

        var result = await client.GetIntuneHealthAsync();

        Assert.Equal("error", result.Status);
        Assert.Equal("upstream_unavailable", result.Errors[0].Code);
        Assert.Equal(4, h.Handler.Requests.Count); // 1 Versuch + 3 Wiederholungen, nie mehr
    }

    [Fact]
    public async Task Transient_503_then_success_returns_data()
    {
        var (client, h) = T.GraphClient((_, n) => T.Task_(n < 3 ? T.Status(HttpStatusCode.ServiceUnavailable) : T.Page(Overviews)));

        var result = await client.GetIntuneHealthAsync();

        Assert.Equal("ok", result.Status);
        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.Equal(2, h.Delays.Count);
    }

    [Fact]
    public async Task Network_error_is_retried_and_then_succeeds()
    {
        var (client, h) = T.GraphClient((_, n) => n == 1 ? throw T.ConnectionReset() : T.Task_(T.Page(Overviews)));

        var result = await client.GetIntuneHealthAsync();

        Assert.Equal("ok", result.Status);
        Assert.Equal(2, h.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("<html>kein json</html>")]
    [InlineData("{\"value\":\"kein-array\"}")]
    [InlineData("{\"unerwartet\":true}")]
    [InlineData("[1,2,3]")]
    public async Task Invalid_or_unexpected_response_is_reported_as_invalid_response(string body)
    {
        var (client, h) = T.GraphClient((_, _) => T.Task_(T.Json(body)));

        var result = await client.GetIntuneIncidentsAsync(false, null);

        Assert.Equal("error", result.Status);
        Assert.Equal("invalid_response", result.Errors[0].Code);
        Assert.Single(h.Handler.Requests); // kein Retry bei strukturell ungueltiger Antwort
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_and_not_retried()
    {
        using var cts = new CancellationTokenSource();
        var (client, h) = T.GraphClient((_, _) =>
        {
            cts.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetIntuneHealthAsync(cts.Token));

        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task Request_timeout_is_reported_as_timeout_after_bounded_retries()
    {
        var (client, h) = T.GraphClient(
            (_, _) => new TaskCompletionSource<HttpResponseMessage>().Task, // antwortet nie
            o => o.RequestTimeoutSeconds = 1);

        var result = await client.GetIntuneHealthAsync();

        Assert.Equal("timeout", result.Errors[0].Code);
        Assert.Equal(4, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Disabled_feature_makes_no_http_calls()
    {
        var (client, h) = T.GraphClient((_, _) => T.Task_(T.Page(Overviews)), o => o.EnableIntune = false);

        var health = await client.GetIntuneHealthAsync();
        var messages = await client.GetMessagesAsync(true, null, null, null);

        Assert.Equal("disabled", health.Status);
        Assert.Equal("disabled", messages.Status);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task Uses_dedicated_http_client_without_redirects_name()
    {
        var (client, h) = T.GraphClient((_, _) => T.Task_(T.Page(Overviews)));

        await client.GetIntuneHealthAsync();

        Assert.Equal(ApiServer.Health.HealthRequestRunner.HttpClientName, h.Factory.LastName);
    }
}
