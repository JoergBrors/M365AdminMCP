using System.Text.Json;
using System.Text.RegularExpressions;
using ApiServer.Auth;
using ApiServer.Health.Models;
using Microsoft.Extensions.Options;

namespace ApiServer.Health;

/// <summary>
/// Microsoft Graph Service Communications (v1.0): healthOverviews, issues, messages.
/// Intune wird ueber die exakten Felder erkannt (healthOverview.id == "Intune" bzw. service == "Microsoft Intune";
/// issue.service == "Microsoft Intune"; message.services enthaelt "Microsoft Intune"), nicht ueber Teilstring-Suche.
/// Es werden nur dokumentierte Abfragen gestellt; Filterung/Sortierung erfolgt clientseitig.
/// </summary>
public partial class MicrosoftServiceHealthClient
{
    public const string IntuneServiceName = "Microsoft Intune";
    public const string IntuneServiceId = "Intune";
    private const string GraphBase = "https://graph.microsoft.com/v1.0/admin/serviceAnnouncement";
    private static readonly IReadOnlySet<string> GraphHosts = new HashSet<string> { "graph.microsoft.com" };

    private readonly HealthRequestRunner _runner;
    private readonly GraphTokenService _tokenService;
    private readonly MicrosoftHealthOptions _options;
    private readonly TimeProvider _time;

    public MicrosoftServiceHealthClient(
        HealthRequestRunner runner,
        GraphTokenService tokenService,
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

    public async Task<HealthResponse<ServiceHealthItem>> GetIntuneHealthAsync(CancellationToken cancellationToken = default)
    {
        const string source = "graph.healthOverviews";
        if (Disabled<ServiceHealthItem>(source, out var disabled)) return disabled;

        try
        {
            var paged = await _runner.GetPagedAsync(
                new Uri($"{GraphBase}/healthOverviews"), _tokenService.GetGraphTokenAsync, GraphHosts, source,
                _options.MaxResults, IsIntuneOverview, cancellationToken);

            var items = paged.Items.Select(e => new ServiceHealthItem(
                String(e, "id"), String(e, "service"), String(e, "status"),
                string.Equals(String(e, "status"), "serviceOperational", StringComparison.OrdinalIgnoreCase))).ToList();

            if (items.Count == 0)
            {
                // Keine Aussage ueber den Zustand moeglich - ausdruecklich NICHT "gesund" melden.
                return new HealthResponse<ServiceHealthItem>(source, Now, TenantId, null, "no_data", [], 0,
                    [new HealthError("intune_service_not_found",
                        $"In healthOverviews wurde kein Intune-Dienst gefunden (id '{IntuneServiceId}' / service '{IntuneServiceName}'). Es kann keine Aussage zum Zustand getroffen werden.")],
                    false, false, null);
            }

            return HealthResponse<ServiceHealthItem>.Success(source, Now, TenantId, null, items, paged.Truncated, paged.TruncationReason);
        }
        catch (HealthApiException ex)
        {
            return Failure<ServiceHealthItem>(source, null, ex);
        }
    }

    public async Task<HealthResponse<ServiceIncidentItem>> GetIntuneIncidentsAsync(
        bool activeOnly, int? maxResults, CancellationToken cancellationToken = default)
    {
        const string source = "graph.issues";
        if (Disabled<ServiceIncidentItem>(source, out var disabled)) return disabled;
        var limit = Limit(maxResults);

        try
        {
            // Sortierung erfolgt nach dem Abruf; deshalb zuerst alle passenden Eintraege (bis MaxResults) sammeln.
            var paged = await _runner.GetPagedAsync(
                new Uri($"{GraphBase}/issues"), _tokenService.GetGraphTokenAsync, GraphHosts, source,
                _options.MaxResults, e => IsIntuneIssue(e) && (!activeOnly || !Bool(e, "isResolved")), cancellationToken);

            var items = paged.Items.Select(MapIncident)
                .OrderByDescending(i => i.LastModifiedDateTime ?? i.StartDateTime ?? DateTimeOffset.MinValue)
                .ToList();

            var truncated = paged.Truncated || items.Count > limit;
            var reason = paged.TruncationReason ?? (items.Count > limit ? $"maxResults ({limit}) erreicht; weitere Eintraege vorhanden." : null);
            return HealthResponse<ServiceIncidentItem>.Success(source, Now, TenantId, null, items.Take(limit).ToList(), truncated, reason);
        }
        catch (HealthApiException ex)
        {
            return Failure<ServiceIncidentItem>(source, null, ex);
        }
    }

    public async Task<HealthResponse<ServiceMessageItem>> GetMessagesAsync(
        bool onlyIntune, DateTime? sinceUtc, string? serviceName, int? maxResults, CancellationToken cancellationToken = default)
    {
        const string source = "graph.messages";
        if (Disabled<ServiceMessageItem>(source, out var disabled)) return disabled;
        var limit = Limit(maxResults);

        try
        {
            var paged = await _runner.GetPagedAsync(
                new Uri($"{GraphBase}/messages"), _tokenService.GetGraphTokenAsync, GraphHosts, source,
                _options.MaxResults, e => MessageMatches(e, onlyIntune, sinceUtc, serviceName), cancellationToken);

            var items = paged.Items.Select(MapMessage)
                .OrderByDescending(m => m.LastModifiedDateTime ?? m.StartDateTime ?? DateTimeOffset.MinValue)
                .ToList();

            var truncated = paged.Truncated || items.Count > limit;
            var reason = paged.TruncationReason ?? (items.Count > limit ? $"maxResults ({limit}) erreicht; weitere Eintraege vorhanden." : null);
            return HealthResponse<ServiceMessageItem>.Success(source, Now, TenantId, null, items.Take(limit).ToList(), truncated, reason);
        }
        catch (HealthApiException ex)
        {
            return Failure<ServiceMessageItem>(source, null, ex);
        }
    }

    // ---------- Filter ----------

    private static bool IsIntuneOverview(JsonElement e) =>
        string.Equals(String(e, "id"), IntuneServiceId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(String(e, "service"), IntuneServiceName, StringComparison.OrdinalIgnoreCase);

    private static bool IsIntuneIssue(JsonElement e) =>
        string.Equals(String(e, "service"), IntuneServiceName, StringComparison.OrdinalIgnoreCase);

    private static bool MessageMatches(JsonElement e, bool onlyIntune, DateTime? sinceUtc, string? serviceName)
    {
        var services = StringList(e, "services");

        if (onlyIntune && !services.Contains(IntuneServiceName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(serviceName) && !services.Contains(serviceName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (sinceUtc is { } since)
        {
            var modified = Date(e, "lastModifiedDateTime") ?? Date(e, "startDateTime");
            if (modified is null || modified.Value.UtcDateTime < since)
            {
                return false;
            }
        }

        return true;
    }

    // ---------- Mapping (toleriert fehlende Felder) ----------

    private static ServiceIncidentItem MapIncident(JsonElement e) => new(
        String(e, "id"), String(e, "title"), String(e, "service"), String(e, "status"), String(e, "classification"),
        String(e, "origin"), Bool(e, "isResolved"), Date(e, "startDateTime"), Date(e, "endDateTime"),
        Date(e, "lastModifiedDateTime"), String(e, "feature"), String(e, "featureGroup"), String(e, "impactDescription"));

    private static ServiceMessageItem MapMessage(JsonElement e)
    {
        var actionBy = Date(e, "actionRequiredByDateTime");
        var tags = StringList(e, "tags");
        return new ServiceMessageItem(
            String(e, "id"), String(e, "title"), StringList(e, "services"), String(e, "category"), String(e, "severity"),
            Date(e, "startDateTime"), Date(e, "endDateTime"), Date(e, "lastModifiedDateTime"), Bool(e, "isMajorChange"),
            actionBy is not null, // nur das von der API gelieferte Feld actionRequiredByDateTime gilt als Handlungsbedarf
            actionBy, tags, Preview(e));
    }

    private static string? Preview(JsonElement e)
    {
        if (!e.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) return null;
        var content = String(body, "content");
        if (string.IsNullOrWhiteSpace(content)) return null;
        var text = WhitespaceRegex().Replace(TagRegex().Replace(content, " "), " ").Trim();
        return text.Length > 500 ? text[..500] + "..." : text;
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();

    private static string? String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        String(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static List<string> StringList(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    // ---------- Hilfen ----------

    private int Limit(int? maxResults) => Math.Clamp(maxResults ?? _options.DefaultMaxResults, 1, _options.MaxResults);

    private bool Disabled<T>(string source, out HealthResponse<T> response)
    {
        if (!_options.Enabled || !_options.EnableIntune)
        {
            response = HealthResponse<T>.Disabled(source, Now, TenantId, null,
                "Die Microsoft-Health-Funktion fuer Intune/Microsoft 365 ist deaktiviert (MicrosoftHealth:Enabled / EnableIntune).");
            return true;
        }

        response = default!;
        return false;
    }

    private HealthResponse<T> Failure<T>(string source, string? subscriptionId, HealthApiException ex) =>
        HealthResponse<T>.Failure(source, Now, TenantId, subscriptionId, ex.Code, Friendly(ex), ex.HttpStatus);

    private static string Friendly(HealthApiException ex) => ex.Code switch
    {
        "forbidden" => ex.Message + " Pruefe die Graph-Anwendungsberechtigungen ServiceHealth.Read.All bzw. ServiceMessage.Read.All und deren Administratorzustimmung.",
        "unauthorized" => ex.Message + " Pruefe die Identitaet der API (Managed Identity/Client Secret) und die Token-Audience.",
        _ => ex.Message
    };
}
