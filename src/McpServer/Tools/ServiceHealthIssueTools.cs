using System.ComponentModel;
using System.Text.Json;
using McpServer.Services;
using McpServer.Utils;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

/// <summary>
/// Kompakte Suche, Einzelabruf und verlustfreier, abschnittsweiser Content-Abruf fuer Microsoft-365-Service-Health-Issues.
/// Ergaenzt get_service_health_issues (unveraendert), dessen Rohantwort bei grossen Tenants das Antwortlimit sprengen kann.
/// Alle Tools sind schreibgeschuetzt; Graph wird ausschliesslich vom ApiServer aufgerufen. Eingaben werden hier vorab und im
/// ApiServer verbindlich geprueft.
/// </summary>
[McpServerToolType]
public class ServiceHealthIssueTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const string Base = "/api/m365/status/issues";

    private readonly ApiServerClient _apiServerClient;

    public ServiceHealthIssueTools(ApiServerClient apiServerClient)
    {
        _apiServerClient = apiServerClient;
    }

    [McpServerTool(Name = "search_service_health_issues"), Description(
        "Durchsucht Microsoft-365-Service-Health-Issues kompakt (ohne lange Beitragstexte) mit Filtern und Seitenweise-Abruf. " +
        "Jedes Ergebnis enthält contentRefs für den vollständigen Inhalt (get_service_health_issue_content). " +
        "hasMore=true bedeutet: weitere Ergebnisse mit continuationToken abrufen (gleiche Filter!). " +
        "truncated/isPartial/scanComplete zeigen, ob die Suche vollständig ist.")]
    public Task<string> SearchServiceHealthIssues(
        [Description("Exakter Dienstname, z. B. 'Exchange Online'.")] string? service = null,
        [Description("true = nur gelöste, false = nur offene Issues.")] bool? isResolved = null,
        [Description("incident oder advisory.")] string? classification = null,
        [Description("Status, z. B. investigating, serviceDegradation, serviceRestored.")] string? status = null,
        [Description("Nur Issues mit Startzeit ab diesem Zeitpunkt (ISO 8601, UTC).")] string? startFrom = null,
        [Description("Nur Issues mit Startzeit bis zu diesem Zeitpunkt (ISO 8601, UTC).")] string? startTo = null,
        [Description("Freitext in Titel, Auswirkung, Dienst, Feature oder ID (lokale Suche, max. 100 Zeichen).")] string? searchText = null,
        [Description("Maximale Anzahl Ergebnisse (1-100). Standard: 25.")] int? maxResults = null,
        [Description("continuationToken aus der vorherigen Antwort; Filter müssen unverändert bleiben.")] string? continuationToken = null)
    {
        if (!IssueInputValidator.TryNormalizeText(service, "service", out var svc, out var error) ||
            !IssueInputValidator.TryParseClassification(classification, out var cls, out error) ||
            !IssueInputValidator.TryParseStatus(status, out var st, out error) ||
            !IssueInputValidator.TryParseDate(startFrom, "startFrom", out var from, out error) ||
            !IssueInputValidator.TryParseDate(startTo, "startTo", out var to, out error) ||
            !IssueInputValidator.TryNormalizeText(searchText, "searchText", out var text, out error) ||
            !IssueInputValidator.TryNormalizeMaxResults(maxResults, out var max, out error))
            return Task.FromResult(Invalid(error));

        if (continuationToken is { Length: > 4096 })
            return Task.FromResult(Invalid("continuationToken ist ungültig."));

        var path = $"{Base}/search?maxResults={max}";
        if (svc is not null) path += $"&service={Uri.EscapeDataString(svc)}";
        if (isResolved is { } r) path += $"&isResolved={(r ? "true" : "false")}";
        if (cls is not null) path += $"&classification={cls}";
        if (st is not null) path += $"&status={st}";
        if (from is { } f) path += $"&startFrom={Uri.EscapeDataString(f.ToString("o"))}";
        if (to is { } t) path += $"&startTo={Uri.EscapeDataString(t.ToString("o"))}";
        if (text is not null) path += $"&searchText={Uri.EscapeDataString(text)}";
        if (!string.IsNullOrEmpty(continuationToken)) path += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
        return _apiServerClient.GetAsync(path);
    }

    [McpServerTool(Name = "get_service_health_issue"), Description(
        "Liefert ein einzelnes Service-Health-Issue per ID. projection: summary (Standard), details (Metadaten, Beitrags-Übersicht, contentRefs) " +
        "oder full (alles inline, nur wenn es in eine Antwort passt – sonst isPartial/hasMore und Inhalte über get_service_health_issue_content). " +
        "contentVersion ändert sich, sobald Microsoft das Issue ändert.")]
    public Task<string> GetServiceHealthIssue(
        [Description("Issue-ID, z. B. EX226792.")] string id,
        [Description("summary, details oder full. Standard: summary.")] string? projection = null)
    {
        if (!IssueInputValidator.TryParseIssueId(id, out var issueId, out var error) ||
            !IssueInputValidator.TryParseProjection(projection, out var proj, out error))
            return Task.FromResult(Invalid(error));

        return _apiServerClient.GetAsync($"{Base}/{Uri.EscapeDataString(issueId)}?projection={proj}");
    }

    [McpServerTool(Name = "get_service_health_issue_content"), Description(
        "Liefert den vollständigen Inhalt eines Issues (Auswirkung, Details oder einen Beitrag) verlustfrei in Abschnitten. " +
        "Solange hasMore=true: erneut mit demselben contentId und dem zurückgegebenen continuationToken aufrufen und die text-Felder " +
        "in Reihenfolge aneinanderhängen. Meldet content_changed, hat sich der Inhalt geändert – dann ohne Token neu beginnen.")]
    public Task<string> GetServiceHealthIssueContent(
        [Description("contentId aus contentRefs, z. B. issue:EX226792:post:0, issue:EX226792:impact, issue:EX226792:details.")] string contentId,
        [Description("continuationToken der vorherigen Teilantwort.")] string? continuationToken = null)
    {
        if (!IssueInputValidator.TryParseContentId(contentId, out _, out _, out _, out var error))
            return Task.FromResult(Invalid(error));

        if (continuationToken is { Length: > 4096 })
            return Task.FromResult(Invalid("continuationToken ist ungültig."));

        var path = $"{Base}/content?contentId={Uri.EscapeDataString(contentId.Trim())}";
        if (!string.IsNullOrEmpty(continuationToken)) path += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
        return _apiServerClient.GetAsync(path);
    }

    private static string Invalid(string error) => JsonSerializer.Serialize(new
    {
        status = "invalid_request",
        errors = new[] { new { code = "invalid_request", message = error } }
    }, JsonOptions);
}
