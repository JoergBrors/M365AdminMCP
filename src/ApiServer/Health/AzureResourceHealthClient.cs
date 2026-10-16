using System.Globalization;
using System.Text.Json;
using ApiServer.Auth;
using ApiServer.Health.Models;
using McpServer.Utils;
using Microsoft.Extensions.Options;

namespace ApiServer.Health;

/// <summary>
/// Azure Resource Health ueber Azure Resource Manager (api-version 2025-05-01): Service-Health-Events einer Subscription,
/// aktuelle Verfuegbarkeit einer Ressource, Verfuegbarkeit aller Ressourcen einer Subscription.
/// Zugriff nur auf Subscriptions aus MicrosoftHealth:AllowedSubscriptionIds; Hosts sind fest (management.azure.com).
/// </summary>
public class AzureResourceHealthClient
{
    public const string ApiVersion = "2025-05-01";
    private const string ArmBase = "https://management.azure.com";
    private static readonly IReadOnlySet<string> ArmHosts = new HashSet<string> { "management.azure.com" };

    private readonly HealthRequestRunner _runner;
    private readonly ArmTokenService _tokenService;
    private readonly MicrosoftHealthOptions _options;
    private readonly TimeProvider _time;

    public AzureResourceHealthClient(
        HealthRequestRunner runner,
        ArmTokenService tokenService,
        IOptions<MicrosoftHealthOptions> options,
        TimeProvider? timeProvider = null)
    {
        _runner = runner;
        _tokenService = tokenService;
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;
    private string? TenantId => string.IsNullOrWhiteSpace(_options.TenantId) ? null : _options.TenantId;

    /// <summary>Allowlist (normalisiert, Kleinbuchstaben).</summary>
    public IReadOnlyList<string> AllowedSubscriptions => _options.AllowedSubscriptionIds
        .Select(id => HealthInputValidator.TryParseSubscriptionId(id, out var normalized, out _) ? normalized : null)
        .Where(id => id is not null).Select(id => id!).Distinct().ToList();

    public async Task<HealthResponse<AzureHealthEventItem>> GetServiceHealthEventsAsync(
        string subscriptionId, DateTime? sinceUtc, int? maxResults, CancellationToken cancellationToken = default)
    {
        const string source = "arm.events";
        if (Guard<AzureHealthEventItem>(source, subscriptionId, out var normalized, out var blocked)) return blocked;
        var limit = Math.Clamp(maxResults ?? _options.DefaultMaxResults, 1, _options.MaxResults);

        // queryStartTime: laut Doku Datum im Format M/d/yyyy (Tagesgranularitaet), bezogen auf lastUpdateTime; Standard der API: 3 Tage.
        var url = $"{ArmBase}/subscriptions/{normalized}/providers/Microsoft.ResourceHealth/events?api-version={ApiVersion}";
        if (sinceUtc is { } since)
        {
            url += "&queryStartTime=" + Uri.EscapeDataString(since.ToString("M/d/yyyy", CultureInfo.InvariantCulture));
        }

        try
        {
            var paged = await _runner.GetPagedAsync(new Uri(url), _tokenService.GetArmTokenAsync, ArmHosts, source, limit, null, cancellationToken);
            var items = paged.Items.Select(MapEvent)
                .OrderByDescending(e => e.LastUpdateTime ?? e.ImpactStartTime ?? DateTimeOffset.MinValue).ToList();
            return HealthResponse<AzureHealthEventItem>.Success(source, Now, TenantId, normalized, items, paged.Truncated, paged.TruncationReason);
        }
        catch (HealthApiException ex)
        {
            return Failure<AzureHealthEventItem>(source, normalized, ex);
        }
    }

    public async Task<HealthResponse<ResourceAvailabilityItem>> GetResourceAvailabilityAsync(
        string resourceId, CancellationToken cancellationToken = default)
    {
        const string source = "arm.availabilityStatuses.current";
        if (!HealthInputValidator.TryParseResourceId(resourceId, out var normalizedId, out var subscriptionId, out var error))
        {
            return HealthResponse<ResourceAvailabilityItem>.Failure(source, Now, TenantId, null, "invalid_resource_id", error, null, "error");
        }

        if (Guard<ResourceAvailabilityItem>(source, subscriptionId, out _, out var blocked)) return blocked;

        var uri = new Uri($"{ArmBase}{normalizedId}/providers/Microsoft.ResourceHealth/availabilityStatuses/current?api-version={ApiVersion}");
        if (!ArmHosts.Contains(uri.Host))
        {
            return HealthResponse<ResourceAvailabilityItem>.Failure(source, Now, TenantId, subscriptionId, "invalid_resource_id", "Ungueltiger Zielhost.");
        }

        try
        {
            using var document = await _runner.GetJsonAsync(uri, _tokenService.GetArmTokenAsync, source, cancellationToken);
            var item = MapAvailability(document.RootElement);
            if (item is null)
            {
                return HealthResponse<ResourceAvailabilityItem>.Failure(source, Now, TenantId, subscriptionId, "invalid_response",
                    "Die Antwort enthaelt keinen Verfuegbarkeitsstatus.");
            }

            // availabilityState "Unknown" wird nicht als verfuegbar gewertet
            return HealthResponse<ResourceAvailabilityItem>.Success(source, Now, TenantId, subscriptionId, [item]);
        }
        catch (HealthApiException ex) when (ex.Code == "not_found")
        {
            return HealthResponse<ResourceAvailabilityItem>.Failure(source, Now, TenantId, subscriptionId, "resource_not_found",
                "Ressource nicht gefunden oder fuer Resource Health nicht unterstuetzt (HTTP 404).", ex.HttpStatus, "no_data");
        }
        catch (HealthApiException ex)
        {
            return Failure<ResourceAvailabilityItem>(source, subscriptionId, ex);
        }
    }

    public async Task<HealthResponse<ResourceAvailabilityItem>> GetSubscriptionAvailabilityAsync(
        string subscriptionId, int? maxResults, CancellationToken cancellationToken = default)
    {
        const string source = "arm.availabilityStatuses.subscription";
        if (Guard<ResourceAvailabilityItem>(source, subscriptionId, out var normalized, out var blocked)) return blocked;
        var limit = Math.Clamp(maxResults ?? _options.DefaultMaxResults, 1, _options.MaxResults);
        var url = $"{ArmBase}/subscriptions/{normalized}/providers/Microsoft.ResourceHealth/availabilityStatuses?api-version={ApiVersion}";

        try
        {
            var paged = await _runner.GetPagedAsync(new Uri(url), _tokenService.GetArmTokenAsync, ArmHosts, source, limit, null, cancellationToken);
            var items = paged.Items.Select(MapAvailability).Where(i => i is not null).Select(i => i!).ToList();

            // Nur Zaehlung der tatsaechlich gelieferten Eintraege; bei Abschneiden ist sie ausdruecklich unvollstaendig.
            var aggregates = new AvailabilityAggregates(
                Count(items, "Available"), Count(items, "Unavailable"), Count(items, "Degraded"), Count(items, "Unknown"),
                items.Count(i => !new[] { "Available", "Unavailable", "Degraded", "Unknown" }.Contains(i.AvailabilityState, StringComparer.OrdinalIgnoreCase)),
                !paged.Truncated);

            return HealthResponse<ResourceAvailabilityItem>.Success(source, Now, TenantId, normalized, items,
                paged.Truncated, paged.TruncationReason, aggregates);
        }
        catch (HealthApiException ex)
        {
            return Failure<ResourceAvailabilityItem>(source, normalized, ex);
        }
    }

    // ---------- Pruefungen ----------

    private bool Guard<T>(string source, string? subscriptionId, out string normalized, out HealthResponse<T> blocked)
    {
        normalized = string.Empty;

        if (!_options.Enabled || !_options.EnableAzure)
        {
            blocked = HealthResponse<T>.Disabled(source, Now, TenantId, null,
                "Die Azure-Health-Funktion ist deaktiviert (MicrosoftHealth:Enabled / EnableAzure).");
            return true;
        }

        if (!HealthInputValidator.TryParseSubscriptionId(subscriptionId, out normalized, out var error))
        {
            blocked = HealthResponse<T>.Failure(source, Now, TenantId, null, "invalid_subscription_id", error);
            return true;
        }

        var allowed = AllowedSubscriptions;
        if (allowed.Count == 0)
        {
            blocked = HealthResponse<T>.Disabled(source, Now, TenantId, normalized,
                "Es sind keine Subscriptions freigegeben (MicrosoftHealth:AllowedSubscriptionIds ist leer).");
            return true;
        }

        if (!allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            blocked = HealthResponse<T>.Failure(source, Now, TenantId, normalized, "subscription_not_allowed",
                "Die Subscription ist nicht in MicrosoftHealth:AllowedSubscriptionIds freigegeben.");
            return true;
        }

        blocked = default!;
        return false;
    }

    private HealthResponse<T> Failure<T>(string source, string? subscriptionId, HealthApiException ex) =>
        HealthResponse<T>.Failure(source, Now, TenantId, subscriptionId, ex.Code, Friendly(ex), ex.HttpStatus);

    private static string Friendly(HealthApiException ex) => ex.Code switch
    {
        "forbidden" => ex.Message + " Der API-Identitaet fehlt die Azure-RBAC-Berechtigung (z. B. Rolle 'Service Health Security Reader') auf der Subscription.",
        "unauthorized" => ex.Message + " Pruefe die Identitaet der API (Managed Identity/Client Secret).",
        _ => ex.Message
    };

    private static int Count(List<ResourceAvailabilityItem> items, string state) =>
        items.Count(i => string.Equals(i.AvailabilityState, state, StringComparison.OrdinalIgnoreCase));

    // ---------- Mapping (toleriert fehlende Felder) ----------

    private static AzureHealthEventItem MapEvent(JsonElement e)
    {
        var p = Obj(e, "properties");
        var impacted = new List<AzureImpactedService>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("impact", out var impact) && impact.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in impact.EnumerateArray())
            {
                var regions = new List<string>();
                if (i.ValueKind == JsonValueKind.Object && i.TryGetProperty("impactedRegions", out var r) && r.ValueKind == JsonValueKind.Array)
                {
                    regions.AddRange(r.EnumerateArray().Select(x => Str(x, "impactedRegion")).Where(x => x is not null).Select(x => x!));
                }

                impacted.Add(new AzureImpactedService(Str(i, "impactedService"), regions));
            }
        }

        return new AzureHealthEventItem(
            Str(e, "name"), Str(p, "eventType"), Str(p, "eventSource"), Str(p, "status"), Str(p, "level") ?? Str(p, "eventLevel"),
            Str(p, "title"), Str(p, "summary"), Date(p, "impactStartTime"), Date(p, "impactMitigationTime"), Date(p, "lastUpdateTime"),
            p.ValueKind == JsonValueKind.Object && p.TryGetProperty("priority", out var pr) && pr.TryGetInt32(out var prio) ? prio : null,
            p.ValueKind == JsonValueKind.Object && p.TryGetProperty("isEventSensitive", out var s) && s.ValueKind == JsonValueKind.True,
            impacted,
            p.ValueKind == JsonValueKind.Object && p.TryGetProperty("eventTags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList()
                : []);
    }

    private static ResourceAvailabilityItem? MapAvailability(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var p = Obj(e, "properties");
        var state = Str(p, "availabilityState");
        // Laut Doku kann availabilityState fehlen (dann beschreibt der Eintrag ein verfuegbarkeitsbeeintraechtigendes Ereignis).
        // Die API liefert die ID der Verfuegbarkeitsstatus-Unterressource; fuer die Ausgabe wird die Ressource selbst gemeldet.
        const string suffix = "/providers/Microsoft.ResourceHealth/availabilityStatuses/current";
        var id = Str(e, "id");
        if (id is not null && id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^suffix.Length];
        }

        return new ResourceAvailabilityItem(
            id, Str(e, "location"), state ?? "Unknown", Str(p, "title"), Str(p, "summary"), Str(p, "detailedStatus"),
            Str(p, "reasonType"), Date(p, "occuredTime") ?? Date(p, "occurredTime"), Date(p, "reportedTime"), Date(p, "resolutionETA"),
            Str(p, "healthEventId"));
    }

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
