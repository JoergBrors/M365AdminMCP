using System.ComponentModel;
using System.Text.Json;
using McpServer.Services;
using McpServer.Utils;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Intune-/Microsoft-365-Service-Health (Microsoft Graph) und Azure Service/Resource Health (Azure Resource Manager).
/// Alle Tools sind schreibgeschuetzt und rufen den zentralen ApiServer auf, der die Identitaet und die Berechtigungen haelt.
/// Eingaben werden hier vorab geprueft, damit das LLM eine lesbare Fehlermeldung erhaelt; der ApiServer prueft verbindlich erneut.
/// </summary>
[McpServerToolType]
public class MicrosoftHealthTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const string Base = "/api/microsoft-health";

    private readonly ApiServerClient _apiServerClient;

    public MicrosoftHealthTools(ApiServerClient apiServerClient)
    {
        _apiServerClient = apiServerClient;
    }

    [McpServerTool(Name = "intune_service_health"), Description(
        "Liefert den von Microsoft gemeldeten aktuellen Service-Health-Zustand von Microsoft Intune (Graph healthOverviews). " +
        "status beschreibt den Datenabruf (ok/no_data/error/disabled), nicht die Gesundheit; die Dienststatus stehen in items[].status.")]
    public Task<string> IntuneServiceHealth() => _apiServerClient.GetAsync($"{Base}/intune/health");

    [McpServerTool(Name = "intune_service_incidents"), Description(
        "Liefert Microsoft-Intune-Vorfaelle (Service Incidents/Advisories) aus Microsoft Graph, neueste Aenderung zuerst.")]
    public Task<string> IntuneServiceIncidents(
        [Description("Nur aktive (nicht gelöste) Vorfälle. Standard: false (aktive und historische).")] bool activeOnly = false,
        [Description("Maximale Anzahl Ergebnisse (1-1000). Standard: 100.")] int? maxResults = null)
    {
        if (!HealthInputValidator.TryNormalizeMaxResults(maxResults, HealthInputValidator.DefaultMaxResults, HealthInputValidator.HardMaxResults, out var max, out var error))
            return Task.FromResult(Invalid(error));

        return _apiServerClient.GetAsync($"{Base}/intune/incidents?activeOnly={Bool(activeOnly)}&maxResults={max}");
    }

    [McpServerTool(Name = "microsoft_service_messages"), Description(
        "Liefert Message-Center-Meldungen (Ankündigungen) aus Microsoft Graph. Standardmäßig nur Intune-relevante; " +
        "mit onlyIntune=false alle Microsoft-365-Dienste. Handlungsbedarf ist über actionRequired/actionRequiredByDateTime erkennbar.")]
    public Task<string> MicrosoftServiceMessages(
        [Description("Nur Meldungen zu Microsoft Intune. Standard: true.")] bool onlyIntune = true,
        [Description("Nur Meldungen, die seit diesem Zeitpunkt (ISO 8601, UTC, z. B. 2026-10-01T00:00:00Z) geändert wurden.")] string? sinceUtc = null,
        [Description("Optional: nur Meldungen zu diesem Dienstnamen (exakt, z. B. 'Exchange Online').")] string? serviceName = null,
        [Description("Maximale Anzahl Ergebnisse (1-1000). Standard: 100.")] int? maxResults = null)
    {
        if (!HealthInputValidator.TryParseSinceUtc(sinceUtc, DateTime.UtcNow, out var since, out var error) ||
            !HealthInputValidator.TryNormalizeMaxResults(maxResults, HealthInputValidator.DefaultMaxResults, HealthInputValidator.HardMaxResults, out var max, out error))
            return Task.FromResult(Invalid(error));

        if (serviceName is { Length: > 100 })
            return Task.FromResult(Invalid("serviceName ist zu lang (max. 100 Zeichen)."));

        var path = $"{Base}/messages?onlyIntune={Bool(onlyIntune)}&maxResults={max}";
        if (since is { } s) path += $"&sinceUtc={Uri.EscapeDataString(s.ToString("o"))}";
        if (!string.IsNullOrWhiteSpace(serviceName)) path += $"&serviceName={Uri.EscapeDataString(serviceName.Trim())}";
        return _apiServerClient.GetAsync(path);
    }

    [McpServerTool(Name = "azure_service_health"), Description(
        "Liefert Azure-Service-Health-Ereignisse (Störungen, geplante Wartung, Hinweise) einer freigegebenen Subscription " +
        "(Azure Resource Health Events API). Standardzeitraum der API: letzte 3 Tage; sinceUtc hat Tagesgranularität.")]
    public Task<string> AzureServiceHealth(
        [Description("Subscription-ID (GUID). Muss in der Server-Allowlist freigegeben sein.")] string subscriptionId,
        [Description("Nur Ereignisse seit diesem Zeitpunkt (ISO 8601, UTC); es wird das Datum verwendet.")] string? sinceUtc = null,
        [Description("Maximale Anzahl Ergebnisse (1-1000). Standard: 100.")] int? maxResults = null)
    {
        if (!HealthInputValidator.TryParseSubscriptionId(subscriptionId, out var sub, out var error) ||
            !HealthInputValidator.TryParseSinceUtc(sinceUtc, DateTime.UtcNow, out var since, out error) ||
            !HealthInputValidator.TryNormalizeMaxResults(maxResults, HealthInputValidator.DefaultMaxResults, HealthInputValidator.HardMaxResults, out var max, out error))
            return Task.FromResult(Invalid(error));

        var path = $"{Base}/azure/events?subscriptionId={sub}&maxResults={max}";
        if (since is { } s) path += $"&sinceUtc={Uri.EscapeDataString(s.ToString("o"))}";
        return _apiServerClient.GetAsync(path);
    }

    [McpServerTool(Name = "azure_resource_health"), Description(
        "Liefert die aktuelle Verfügbarkeit (Resource Health) einer einzelnen Azure-Ressource. " +
        "Es wird eine vollständige Resource-ID erwartet, keine URL.")]
    public Task<string> AzureResourceHealth(
        [Description("Vollständige Azure-Resource-ID, z. B. /subscriptions/{guid}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{name}.")] string resourceId)
    {
        if (!HealthInputValidator.TryParseResourceId(resourceId, out var normalized, out _, out var error))
            return Task.FromResult(Invalid(error));

        return _apiServerClient.GetAsync($"{Base}/azure/resource?resourceId={Uri.EscapeDataString(normalized)}");
    }

    [McpServerTool(Name = "azure_subscription_resource_health"), Description(
        "Liefert die Resource-Health-Verfügbarkeit aller Ressourcen einer freigegebenen Subscription inklusive Zählung nach " +
        "Zustand (nur auf Basis der gelieferten Daten; aggregates.basedOnCompleteData zeigt, ob die Zählung vollständig ist).")]
    public Task<string> AzureSubscriptionResourceHealth(
        [Description("Subscription-ID (GUID). Muss in der Server-Allowlist freigegeben sein.")] string subscriptionId,
        [Description("Maximale Anzahl Ergebnisse (1-1000). Standard: 100.")] int? maxResults = null)
    {
        if (!HealthInputValidator.TryParseSubscriptionId(subscriptionId, out var sub, out var error) ||
            !HealthInputValidator.TryNormalizeMaxResults(maxResults, HealthInputValidator.DefaultMaxResults, HealthInputValidator.HardMaxResults, out var max, out error))
            return Task.FromResult(Invalid(error));

        return _apiServerClient.GetAsync($"{Base}/azure/subscription-resources?subscriptionId={sub}&maxResults={max}");
    }

    [McpServerTool(Name = "microsoft_health_summary"), Description(
        "Fasst Intune Service Health, aktive Intune-Vorfälle, Intune-Meldungen (30 Tage) und Azure Service Health der freigegebenen " +
        "Subscriptions zusammen. Teilfehler sind isoliert (sources[] zeigt je Quelle Status und Abfragezeit). " +
        "Es gibt keine globale Gesundheitsbewertung; Incident, Announcement und Resource Availability bleiben getrennt.")]
    public Task<string> MicrosoftHealthSummary() => _apiServerClient.GetAsync($"{Base}/summary");

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Invalid(string error) => JsonSerializer.Serialize(new
    {
        status = "invalid_request",
        errors = new[] { new { code = "invalid_request", message = error } }
    }, JsonOptions);
}
