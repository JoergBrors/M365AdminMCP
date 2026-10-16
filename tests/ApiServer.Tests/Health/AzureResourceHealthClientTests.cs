using System.Net;
using ApiServer.Health.Models;

namespace ApiServer.Tests.Health;

/// <summary>Azure Resource Health (ARM, api-version 2025-05-01) mit simulierten Antworten.</summary>
public class AzureResourceHealthClientTests
{
    private static readonly string ResourceId = $"/subscriptions/{T.SubA}/resourceGroups/rg-demo/providers/Microsoft.Web/sites/site-1";

    private static string EventJson(string name, string status, string lastUpdate, string service = "Virtual Machines") =>
        $"{{\"name\":\"{name}\",\"type\":\"/providers/Microsoft.ResourceHealth/events\",\"id\":\"/providers/Microsoft.ResourceHealth/events/{name}\"," +
        $"\"properties\":{{\"eventType\":\"ServiceIssue\",\"eventSource\":\"ServiceHealth\",\"status\":\"{status}\",\"level\":\"Warning\"," +
        $"\"title\":\"Titel {name}\",\"summary\":\"Summary {name}\",\"impactStartTime\":\"2026-10-10T10:00:00Z\",\"lastUpdateTime\":\"{lastUpdate}\"," +
        $"\"priority\":2,\"isEventSensitive\":false,\"eventTags\":[\"Action Recommended\"]," +
        $"\"impact\":[{{\"impactedService\":\"{service}\",\"impactedRegions\":[{{\"impactedRegion\":\"West Europe\"}},{{\"impactedRegion\":\"North Europe\"}}]}}]}}}}";

    private static string Availability(string resourceId, string? state, string? title = "Titel") =>
        $"{{\"id\":\"{resourceId}/providers/Microsoft.ResourceHealth/availabilityStatuses/current\",\"name\":\"current\"," +
        "\"type\":\"Microsoft.ResourceHealth/AvailabilityStatuses\",\"location\":\"westeurope\",\"properties\":{" +
        (state is null ? "" : $"\"availabilityState\":\"{state}\",") +
        $"\"title\":\"{title}\",\"summary\":\"Summary\",\"reasonType\":\"Unplanned\",\"occuredTime\":\"2026-10-10T09:00:00Z\",\"reportedTime\":\"2026-10-16T11:00:00Z\"}}}}";

    // ---------- Service Health Events ----------

    [Fact]
    public async Task Events_success_maps_fields_and_builds_documented_request()
    {
        var (client, h) = T.AzureClient((_, _) => T.Task_(T.Page(
            EventJson("E1", "Active", "2026-10-15T00:00:00Z") + "," + EventJson("E2", "Resolved", "2026-10-16T00:00:00Z"), nextLinkProperty: "nextLink")));

        var result = await client.GetServiceHealthEventsAsync(T.SubA.ToUpperInvariant(), null, null);

        Assert.Equal("ok", result.Status);
        Assert.Equal(T.SubA, result.SubscriptionId);
        Assert.Equal(["E2", "E1"], result.Items.Select(e => e.Id)); // neueste Aenderung zuerst
        var first = result.Items[1];
        Assert.Equal("ServiceIssue", first.EventType);
        Assert.Equal("Active", first.Status);
        Assert.Equal(2, first.Priority);
        var impacted = Assert.Single(first.ImpactedServices);
        Assert.Equal("Virtual Machines", impacted.Service);
        Assert.Equal(["West Europe", "North Europe"], impacted.Regions);

        var url = h.Handler.Requests[0].RequestUri!;
        Assert.Equal("management.azure.com", url.Host);
        Assert.Equal($"/subscriptions/{T.SubA}/providers/Microsoft.ResourceHealth/events", url.AbsolutePath);
        Assert.Contains("api-version=2025-05-01", url.Query);
        Assert.Equal("Bearer arm-test-token", h.Handler.Authorizations[0]);
    }

    [Fact]
    public async Task Events_sinceUtc_is_sent_as_queryStartTime_in_documented_date_format()
    {
        var (client, h) = T.AzureClient((_, _) => T.Task_(T.Page("")));

        await client.GetServiceHealthEventsAsync(T.SubA, new DateTime(2026, 10, 3, 17, 30, 0, DateTimeKind.Utc), null);

        Assert.Contains("queryStartTime=10%2F3%2F2026", h.Handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task Events_empty_list_is_no_data()
    {
        var (client, _) = T.AzureClient((_, _) => T.Task_(T.Page("")));

        var result = await client.GetServiceHealthEventsAsync(T.SubA, null, null);

        Assert.Equal("no_data", result.Status);
        Assert.Equal(0, result.ItemCount);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Events_follow_arm_pagination_and_stop_at_maxResults()
    {
        var (client, h) = T.AzureClient((_, n) => T.Task_(n == 1
            ? T.Page(EventJson("E1", "Active", "2026-10-01T00:00:00Z") + "," + EventJson("E2", "Active", "2026-10-02T00:00:00Z"),
                $"https://management.azure.com/subscriptions/{T.SubA}/providers/Microsoft.ResourceHealth/events?api-version=2025-05-01&$skipToken=2", "nextLink")
            : T.Page(EventJson("E3", "Active", "2026-10-03T00:00:00Z"), nextLinkProperty: "nextLink")));

        var all = await client.GetServiceHealthEventsAsync(T.SubA, null, null);
        Assert.Equal(3, all.ItemCount);
        Assert.Equal(2, h.Handler.Requests.Count);

        var (limited, _) = T.AzureClient((_, _) => T.Task_(T.Page(
            EventJson("E1", "Active", "2026-10-01T00:00:00Z") + "," + EventJson("E2", "Active", "2026-10-02T00:00:00Z"), nextLinkProperty: "nextLink")));
        var truncated = await limited.GetServiceHealthEventsAsync(T.SubA, null, 1);
        Assert.Equal(1, truncated.ItemCount);
        Assert.True(truncated.Truncated);
    }

    [Fact]
    public async Task Events_reject_foreign_next_link_host()
    {
        var (client, h) = T.AzureClient((_, _) => T.Task_(T.Page(EventJson("E1", "Active", "2026-10-01T00:00:00Z"),
            "https://management.azure.com.evil.example/next", "nextLink")));

        var result = await client.GetServiceHealthEventsAsync(T.SubA, null, null);

        Assert.Single(h.Handler.Requests);
        Assert.True(result.Truncated);
        Assert.Contains("nextLink", result.TruncationReason);
    }

    // ---------- Eingabe-/Allowlist-Pruefungen ----------

    [Theory]
    [InlineData("")]
    [InlineData("nicht-gueltig")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111-1111-1111-1111-11111111111")]
    public async Task Invalid_subscription_id_is_rejected_without_http_call(string subscriptionId)
    {
        var (client, h) = T.AzureClient((_, _) => T.Task_(T.Page("")));

        var result = await client.GetServiceHealthEventsAsync(subscriptionId, null, null);

        Assert.Equal("invalid_subscription_id", Assert.Single(result.Errors).Code);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task Subscription_outside_allowlist_is_rejected_without_http_call()
    {
        var (client, h) = T.AzureClient((_, _) => T.Task_(T.Page("")));

        var events = await client.GetServiceHealthEventsAsync(T.SubB, null, null);
        var subscription = await client.GetSubscriptionAvailabilityAsync(T.SubB, null);
        var resource = await client.GetResourceAvailabilityAsync($"/subscriptions/{T.SubB}/resourceGroups/rg/providers/Microsoft.Web/sites/x");

        Assert.All(new[] { events.Errors[0], subscription.Errors[0], resource.Errors[0] }, e => Assert.Equal("subscription_not_allowed", e.Code));
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task Empty_allowlist_or_disabled_azure_makes_no_http_calls()
    {
        var (empty, h1) = T.AzureClient((_, _) => T.Task_(T.Page("")), o => o.AllowedSubscriptionIds = []);
        var (off, h2) = T.AzureClient((_, _) => T.Task_(T.Page("")), o => o.EnableAzure = false);

        Assert.Equal("disabled", (await empty.GetServiceHealthEventsAsync(T.SubA, null, null)).Status);
        Assert.Equal("disabled", (await off.GetServiceHealthEventsAsync(T.SubA, null, null)).Status);
        Assert.Empty(h1.Handler.Requests);
        Assert.Empty(h2.Handler.Requests);
    }

    [Theory]
    [InlineData("https://evil.example/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/x")]
    [InlineData("//evil.example/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/x")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/x?api-version=1")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/x#frag")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/../providers/Microsoft.Web/sites/x")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/%2e%2e")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/x\\y")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.ResourceHealth/availabilityStatuses/current")]
    [InlineData("subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/x")]
    [InlineData("")]
    public async Task Invalid_or_manipulated_resource_ids_are_rejected_without_http_call(string resourceId)
    {
        var (client, h) = T.AzureClient((_, _) => T.Task_(T.Json("{}")));

        var result = await client.GetResourceAvailabilityAsync(resourceId);

        Assert.Equal("invalid_resource_id", Assert.Single(result.Errors).Code);
        Assert.Empty(h.Handler.Requests);
    }

    // ---------- Resource Health ----------

    [Fact]
    public async Task Resource_available_is_reported_and_url_is_built_only_from_validated_id()
    {
        var (client, h) = T.AzureClient((_, _) => T.Task_(T.Json(Availability(ResourceId, "Available"))));

        var result = await client.GetResourceAvailabilityAsync(ResourceId);

        Assert.Equal("ok", result.Status);
        var item = Assert.Single(result.Items);
        Assert.Equal("Available", item.AvailabilityState);
        Assert.Equal(ResourceId, item.ResourceId); // Suffix /providers/Microsoft.ResourceHealth/... entfernt
        Assert.Equal("westeurope", item.Location);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), item.OccurredTime);

        var url = h.Handler.Requests[0].RequestUri!;
        Assert.Equal("management.azure.com", url.Host);
        Assert.Equal($"{ResourceId}/providers/Microsoft.ResourceHealth/availabilityStatuses/current", url.AbsolutePath);
        Assert.Contains("api-version=2025-05-01", url.Query);
    }

    [Fact]
    public async Task Resource_unavailable_and_unknown_states_are_preserved()
    {
        var (unavailable, _) = T.AzureClient((_, _) => T.Task_(T.Json(Availability(ResourceId, "Unavailable"))));
        var (unknown, _) = T.AzureClient((_, _) => T.Task_(T.Json(Availability(ResourceId, "Unknown"))));
        var (missing, _) = T.AzureClient((_, _) => T.Task_(T.Json(Availability(ResourceId, null))));

        Assert.Equal("Unavailable", (await unavailable.GetResourceAvailabilityAsync(ResourceId)).Items[0].AvailabilityState);
        Assert.Equal("Unknown", (await unknown.GetResourceAvailabilityAsync(ResourceId)).Items[0].AvailabilityState);
        Assert.Equal("Unknown", (await missing.GetResourceAvailabilityAsync(ResourceId)).Items[0].AvailabilityState); // fehlender Status = nicht verfuegbar-gemeldet
    }

    [Fact]
    public async Task Resource_404_is_reported_as_not_found_or_unsupported_not_as_error()
    {
        var (client, h) = T.AzureClient((_, _) => T.Task_(T.Error(HttpStatusCode.NotFound, "ResourceNotFound", "nicht da")));

        var result = await client.GetResourceAvailabilityAsync(ResourceId);

        Assert.Equal("no_data", result.Status);
        Assert.Equal("resource_not_found", Assert.Single(result.Errors).Code);
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task Missing_rbac_permission_is_explained()
    {
        var (client, _) = T.AzureClient((_, _) => T.Task_(T.Error(HttpStatusCode.Forbidden, "AuthorizationFailed", "does not have authorization")));

        var events = await client.GetServiceHealthEventsAsync(T.SubA, null, null);
        var resource = await client.GetResourceAvailabilityAsync(ResourceId);

        Assert.Equal("forbidden", events.Errors[0].Code);
        Assert.Contains("RBAC", events.Errors[0].Message);
        Assert.Contains("Service Health Security Reader", events.Errors[0].Message);
        Assert.Equal("forbidden", resource.Errors[0].Code);
    }

    [Fact]
    public async Task Resource_response_without_properties_is_still_mapped_defensively()
    {
        var (client, _) = T.AzureClient((_, _) => T.Task_(T.Json("{\"id\":\"x\"}")));

        var result = await client.GetResourceAvailabilityAsync(ResourceId);

        Assert.Equal("Unknown", Assert.Single(result.Items).AvailabilityState);
    }

    // ---------- Subscription-Verfuegbarkeit ----------

    [Fact]
    public async Task Subscription_availability_counts_only_returned_data()
    {
        string R(int i) => $"/subscriptions/{T.SubA}/resourceGroups/rg/providers/Microsoft.Web/sites/s{i}";
        var page = string.Join(",", Availability(R(1), "Available"), Availability(R(2), "Available"), Availability(R(3), "Unavailable"),
            Availability(R(4), "Degraded"), Availability(R(5), "Unknown"), Availability(R(6), "Exotisch"));
        var (client, _) = T.AzureClient((_, _) => T.Task_(T.Page(page, nextLinkProperty: "nextLink")));

        var result = await client.GetSubscriptionAvailabilityAsync(T.SubA, null);

        var aggregates = Assert.IsType<AvailabilityAggregates>(result.Aggregates);
        Assert.Equal(2, aggregates.Available);
        Assert.Equal(1, aggregates.Unavailable);
        Assert.Equal(1, aggregates.Degraded);
        Assert.Equal(1, aggregates.Unknown);
        Assert.Equal(1, aggregates.Other);
        Assert.True(aggregates.BasedOnCompleteData);
        Assert.Equal(6, result.ItemCount);
    }

    [Fact]
    public async Task Subscription_availability_follows_pagination_and_marks_truncated_aggregates()
    {
        string R(int i) => $"/subscriptions/{T.SubA}/resourceGroups/rg/providers/Microsoft.Web/sites/s{i}";
        var (client, h) = T.AzureClient((_, n) => T.Task_(T.Page(Availability(R(n), "Available"),
            $"https://management.azure.com/subscriptions/{T.SubA}/providers/Microsoft.ResourceHealth/availabilityStatuses?api-version=2025-05-01&$skipToken={n}", "nextLink")),
            o => o.MaxPages = 2);

        var result = await client.GetSubscriptionAvailabilityAsync(T.SubA, null);

        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.True(result.Truncated);
        Assert.False(Assert.IsType<AvailabilityAggregates>(result.Aggregates).BasedOnCompleteData);
    }
}
