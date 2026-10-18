using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiServer.Auth;
using ApiServer.Health.Models;
using ApiServer.Services;
using McpServer.Utils;
using Microsoft.Extensions.Options;

namespace ApiServer.Health;

public sealed record IssueQuery(
    string? Service, bool? IsResolved, string? Classification, string? Status,
    DateTimeOffset? StartFrom, DateTimeOffset? StartTo, string? SearchText, int MaxResults);

public sealed record IssueSummary(
    string? Id, string? Title, string? Service, string? Classification, string? Status, bool IsResolved,
    DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime, DateTimeOffset? LastModifiedDateTime,
    string? Feature, string? FeatureGroup, string? ImpactDescription, bool ImpactDescriptionTruncated,
    int PostCount, IReadOnlyList<string> ContentRefs);

/// <summary>Einheitliche Suchantwort. truncated/isPartial/errors folgen dem Health-Antwortmodell (ADR-0016).</summary>
public sealed record IssueSearchResponse(
    string Source, DateTime QueriedAtUtc, string Status, IReadOnlyList<IssueSummary> Items, int ItemCount,
    IReadOnlyList<HealthError> Errors, bool IsPartial, bool Truncated, string? TruncationReason,
    bool HasMore, string? ContinuationToken, string FilterMode, bool ScanComplete, string ListConsistency = "best_effort");

/// <summary>
/// Suche, Einzelabruf und verlustfreier Content-Abruf fuer Service-Health-Issues (Graph v1.0 serviceAnnouncement/issues).
/// Zustandslos: Konsistenz ueber mehrere Aufrufe wird durch contentVersion erkannt (nicht verhindert); es gibt keinen Snapshot-Speicher.
/// Graph wird ausschliesslich ueber HealthRequestRunner (Host-Allowlist, Retry, Zeitlimit, MaxPages) aufgerufen.
/// </summary>
public class ServiceHealthIssueService
{
    private const string Source = "graph.issues";
    private const string IssuesBase = "https://graph.microsoft.com/v1.0/admin/serviceAnnouncement/issues";
    private const int ImpactPreviewChars = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HealthRequestRunner _runner;
    private readonly GraphTokenService _tokenService;
    private readonly MicrosoftHealthOptions _options;
    private readonly ResponseLimitsOptions _limits;
    private readonly ContinuationTokenService _tokens;
    private readonly TimeProvider _time;

    public ServiceHealthIssueService(
        HealthRequestRunner runner, GraphTokenService tokenService, IOptions<MicrosoftHealthOptions> options,
        IOptions<ResponseLimitsOptions> limits, ContinuationTokenService tokens, TimeProvider? time = null)
    {
        _runner = runner;
        _tokenService = tokenService;
        _options = options.Value;
        _limits = limits.Value;
        _tokens = tokens;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ---------- Suche ----------

    public async Task<string> SearchAsync(IssueQuery query, string? continuationToken, CancellationToken cancellationToken)
    {
        var bind = BindHash(query);
        var filterMode = "graph";
        string url;
        var skip = 0;

        if (!string.IsNullOrEmpty(continuationToken))
        {
            if (!_tokens.TryValidate(continuationToken, "search", bind, out var payload, out var tokenError))
            {
                return Error("invalid_continuation_token", tokenError);
            }

            if (payload.Url is null || !IsAllowedIssuesUri(payload.Url, out var resumeUri) || payload.Offset < 0)
            {
                return Error("invalid_continuation_token", "invalid_continuation_token: Token ungueltig.");
            }

            url = resumeUri!.AbsoluteUri;
            skip = payload.Offset;
        }
        else
        {
            url = FirstUrl(query, useGraphFilter: true);
        }

        try
        {
            return Serialize(await ScanAsync(query, bind, url, skip, filterMode, cancellationToken));
        }
        catch (HealthApiException ex) when (ex.Code == "bad_request" && string.IsNullOrEmpty(continuationToken) && (query.Service is not null || query.IsResolved is not null))
        {
            // Graph lehnt den Filter ab: einmal ohne Graph-Filter wiederholen und lokal filtern (kenntlich gemacht).
            try
            {
                return Serialize(await ScanAsync(query, bind, FirstUrl(query, useGraphFilter: false), 0, "local", cancellationToken));
            }
            catch (HealthApiException inner)
            {
                return Error(inner.Code, inner.Message, inner.HttpStatus);
            }
        }
        catch (HealthApiException ex)
        {
            return Error(ex.Code, ex.Message, ex.HttpStatus);
        }
    }

    private async Task<IssueSearchResponse> ScanAsync(IssueQuery query, string bind, string startUrl, int skip, string filterMode, CancellationToken ct)
    {
        var items = new List<IssueSummary>();
        var url = new Uri(startUrl);
        var pages = 0;

        while (true)
        {
            pages++;
            using var document = await _runner.GetJsonAsync(url, _tokenService.GetGraphTokenAsync, Source, ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                throw new HealthApiException("invalid_response", $"Unerwartete Antwortstruktur von {url.Host}: Feld 'value' (Array) fehlt.");
            }

            var nextUri = ReadNext(root, out var nextRejected);
            var index = 0;
            foreach (var element in value.EnumerateArray())
            {
                if (index++ < skip || !Matches(element, query))
                {
                    continue;
                }

                items.Add(ToSummary(element));
                if (items.Count >= query.MaxResults)
                {
                    // Resume-Punkt: Rest dieser Seite, sonst naechste Seite.
                    var more = index < value.GetArrayLength() || nextUri is not null;
                    var token = !more ? null
                        : index < value.GetArrayLength() ? _tokens.Create("search", bind, url.AbsoluteUri, index, null)
                        : _tokens.Create("search", bind, nextUri!.AbsoluteUri, 0, null);
                    return Result(items, more, token, truncated: false, reason: null, scanComplete: !more, filterMode);
                }
            }

            skip = 0;
            if (nextRejected)
            {
                return Result(items, false, null, true, "nextLink wurde abgelehnt (Host/Schema/Pfad nicht erlaubt); Ergebnis unvollstaendig.", false, filterMode);
            }

            if (nextUri is null)
            {
                return Result(items, false, null, false, null, true, filterMode);
            }

            if (pages >= _options.MaxPages)
            {
                // Scanbreite erreicht: weiter moeglich ueber Token, aber nicht vollstaendig durchsucht.
                return Result(items, true, _tokens.Create("search", bind, nextUri.AbsoluteUri, 0, null), true,
                    $"MaxPages ({_options.MaxPages}) erreicht; weitere Seiten vorhanden (continuationToken verwenden).", false, filterMode);
            }

            url = nextUri;
        }
    }

    private IssueSearchResponse Result(List<IssueSummary> items, bool hasMore, string? token, bool truncated, string? reason, bool scanComplete, string filterMode) =>
        new(Source, Now, items.Count == 0 && !hasMore ? "no_data" : "ok", items, items.Count, [], hasMore || truncated, truncated, reason,
            hasMore, token, filterMode, scanComplete);

    private string FirstUrl(IssueQuery q, bool useGraphFilter)
    {
        var clauses = new List<string>();
        if (useGraphFilter && q.Service is not null) clauses.Add($"service eq '{q.Service.Replace("'", "''", StringComparison.Ordinal)}'");
        if (useGraphFilter && q.IsResolved is { } r) clauses.Add($"isResolved eq {(r ? "true" : "false")}");
        return clauses.Count == 0 ? IssuesBase : $"{IssuesBase}?$filter={Uri.EscapeDataString(string.Join(" and ", clauses))}";
    }

    private static bool Matches(JsonElement e, IssueQuery q)
    {
        if (q.Service is not null && !Eq(Str(e, "service"), q.Service)) return false;
        if (q.IsResolved is { } r && Bool(e, "isResolved") != r) return false;
        if (q.Classification is not null && !Eq(Str(e, "classification"), q.Classification)) return false;
        if (q.Status is not null && !Eq(Str(e, "status"), q.Status)) return false;

        if (q.StartFrom is not null || q.StartTo is not null)
        {
            var start = Date(e, "startDateTime");
            if (start is null || (q.StartFrom is { } f && start < f) || (q.StartTo is { } t && start > t)) return false;
        }

        if (q.SearchText is { } text)
        {
            var hit = new[] { "title", "impactDescription", "service", "feature", "featureGroup", "id" }
                .Any(p => Str(e, p) is { } s && s.Contains(text, StringComparison.OrdinalIgnoreCase));
            if (!hit) return false;
        }

        return true;
    }

    private static IssueSummary ToSummary(JsonElement e)
    {
        var id = Str(e, "id");
        var impact = Str(e, "impactDescription");
        var truncated = impact is { Length: > ImpactPreviewChars };
        var postCount = e.TryGetProperty("posts", out var posts) && posts.ValueKind == JsonValueKind.Array ? posts.GetArrayLength() : 0;
        var hasDetails = e.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0;

        var refs = new List<string>();
        if (id is not null)
        {
            if (!string.IsNullOrEmpty(impact)) refs.Add($"issue:{id}:impact");
            if (hasDetails) refs.Add($"issue:{id}:details");
            for (var i = 0; i < postCount; i++) refs.Add($"issue:{id}:post:{i}");
        }

        return new IssueSummary(id, Str(e, "title"), Str(e, "service"), Str(e, "classification"), Str(e, "status"), Bool(e, "isResolved"),
            Date(e, "startDateTime"), Date(e, "endDateTime"), Date(e, "lastModifiedDateTime"), Str(e, "feature"), Str(e, "featureGroup"),
            truncated ? SafePrefix(impact!, ImpactPreviewChars) : impact, truncated, postCount, refs);
    }

    // ---------- Einzelabruf ----------

    public async Task<string> GetIssueAsync(string id, string projection, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await _runner.GetJsonAsync(new Uri($"{IssuesBase}/{Uri.EscapeDataString(id)}"), _tokenService.GetGraphTokenAsync, Source, cancellationToken);
            var e = document.RootElement;
            var summary = ToSummary(e);
            var version = Version(e.GetRawText());

            if (projection == "summary")
            {
                return Serialize(Detail(summary, version, null, null, false, null));
            }

            var posts = new List<object>();
            if (e.TryGetProperty("posts", out var postArray) && postArray.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var p in postArray.EnumerateArray())
                {
                    var content = p.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.Object ? Str(d, "content") : null;
                    posts.Add(new
                    {
                        index = i,
                        contentId = $"issue:{id}:post:{i}",
                        createdDateTime = Date(p, "createdDateTime"),
                        postType = Str(p, "postType"),
                        contentType = p.TryGetProperty("description", out var d2) && d2.ValueKind == JsonValueKind.Object ? Str(d2, "contentType") : null,
                        contentChars = content?.Length ?? 0
                    });
                    i++;
                }
            }

            var details = e.TryGetProperty("details", out var detailsEl) && detailsEl.ValueKind == JsonValueKind.Array ? detailsEl.Clone() : (JsonElement?)null;
            var origin = Str(e, "origin");
            var fullImpact = Str(e, "impactDescription");

            if (projection == "full")
            {
                var inline = new List<object>();
                var j = 0;
                foreach (var p in e.TryGetProperty("posts", out var pa) && pa.ValueKind == JsonValueKind.Array ? pa.EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    inline.Add(new { index = j++, createdDateTime = Date(p, "createdDateTime"), postType = Str(p, "postType"),
                        description = p.TryGetProperty("description", out var dd) ? dd.Clone() : (JsonElement?)null });
                }

                var fullResponse = Serialize(Detail(summary with { ImpactDescription = fullImpact, ImpactDescriptionTruncated = false }, version, details, inline, false, new { origin }));
                if (ResponseSizeGuard.EscapedSize(fullResponse) <= _limits.MaxResponseBytes)
                {
                    return fullResponse;
                }

                // Zu gross: nichts wird verworfen, sondern per get_service_health_issue_content in Teilen abrufbar.
                return Serialize(Detail(summary, version, null, posts, true,
                    new { origin, reason = "Inhalt zu gross fuer eine Antwort; contentRefs einzeln (und ggf. in Teilen) mit get_service_health_issue_content abrufen." }));
            }

            return Serialize(Detail(summary, version, null, posts, false, new { origin }));
        }
        catch (HealthApiException ex)
        {
            return Error(ex.Code, ex.Message, ex.HttpStatus);
        }
    }

    private object Detail(IssueSummary summary, string version, JsonElement? details, object? posts, bool partial, object? extra) => new
    {
        source = Source,
        queriedAtUtc = Now,
        status = "ok",
        issue = summary,
        contentVersion = version,
        details,
        posts,
        extra,
        errors = Array.Empty<HealthError>(),
        isPartial = partial,
        truncated = false,
        hasMore = partial,
        truncationReason = (string?)null
    };

    // ---------- Content (verlustfrei, in Teilen) ----------

    public async Task<string> GetContentAsync(string contentId, string? continuationToken, CancellationToken cancellationToken)
    {
        if (!IssueInputValidator.TryParseContentId(contentId, out var issueId, out var kind, out var postIndex, out var parseError))
        {
            return Error("invalid_request", parseError);
        }

        var offset = 0;
        string? expectedVersion = null;
        if (!string.IsNullOrEmpty(continuationToken))
        {
            if (!_tokens.TryValidate(continuationToken, "content", contentId, out var payload, out var tokenError) || payload.Offset < 0)
            {
                return Error("invalid_continuation_token", tokenError);
            }

            offset = payload.Offset;
            expectedVersion = payload.Version;
        }

        try
        {
            using var document = await _runner.GetJsonAsync(new Uri($"{IssuesBase}/{Uri.EscapeDataString(issueId)}"), _tokenService.GetGraphTokenAsync, Source, cancellationToken);
            var e = document.RootElement;
            string? text;
            string contentType = "text";

            switch (kind)
            {
                case "impact":
                    text = Str(e, "impactDescription");
                    break;
                case "details":
                    text = e.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetRawText() : null;
                    contentType = "json";
                    break;
                default:
                    JsonElement post = default;
                    var found = e.TryGetProperty("posts", out var posts) && posts.ValueKind == JsonValueKind.Array && postIndex < posts.GetArrayLength()
                        && (post = posts[postIndex]).ValueKind == JsonValueKind.Object;
                    text = found && post.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.Object ? Str(desc, "content") : null;
                    contentType = found && post.TryGetProperty("description", out var desc2) && desc2.ValueKind == JsonValueKind.Object ? Str(desc2, "contentType") ?? "text" : "text";
                    break;
            }

            if (text is null)
            {
                return Error("not_found", $"Inhalt '{contentId}' existiert nicht (mehr).", 404);
            }

            var version = Version(Str(e, "lastModifiedDateTime") + "|" + text);
            if (expectedVersion is not null && expectedVersion != version)
            {
                return Error("content_changed", $"Der Inhalt hat sich seit dem ersten Abruf geaendert (neue contentVersion {version}). Ohne continuationToken neu beginnen.", 409);
            }

            if (offset > text.Length)
            {
                return Error("invalid_continuation_token", "invalid_continuation_token: Token ungueltig.");
            }

            return BuildChunk(contentId, contentType, text, offset, version);
        }
        catch (HealthApiException ex)
        {
            return Error(ex.Code, ex.Message, ex.HttpStatus);
        }
    }

    /// <summary>Groesster Abschnitt ab <paramref name="offset"/>, dessen finale Antwort (escaped UTF-8) unter dem Limit bleibt.</summary>
    private string BuildChunk(string contentId, string contentType, string text, int offset, string version)
    {
        int lo = 0, hi = text.Length - offset;
        string Build(int length)
        {
            length = AdjustSurrogate(text, offset, length);
            var end = offset + length;
            var more = end < text.Length;
            return Serialize(new
            {
                source = Source,
                queriedAtUtc = Now,
                status = "ok",
                contentId,
                contentVersion = version,
                contentType,
                offset,
                nextOffset = end,
                totalChars = text.Length,
                text = text.Substring(offset, length),
                hasMore = more,
                isPartial = more,
                truncated = false,
                continuationToken = more ? _tokens.Create("content", contentId, null, end, version) : null,
                errors = Array.Empty<HealthError>()
            });
        }

        var budget = Math.Min(_limits.MaxResponseBytes, _limits.MaxChunkBytes * 2);
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ResponseSizeGuard.EscapedSize(Build(mid)) <= budget) lo = mid; else hi = mid - 1;
        }

        if (lo == 0 && text.Length > offset)
        {
            lo = Math.Min(2, text.Length - offset); // Mindestfortschritt (nur bei absurd kleinem Limit)
        }

        return Build(lo);
    }

    private static int AdjustSurrogate(string text, int offset, int length)
    {
        var end = offset + length;
        return length > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) ? length - 1 : length;
    }

    // ---------- Hilfen ----------

    private bool IsAllowedIssuesUri(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u)) return false;
        if (!IsAllowedNext(u)) return false;
        uri = u;
        return true;
    }

    private static bool IsAllowedNext(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo) && u.IsDefaultPort &&
        string.Equals(u.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase) &&
        u.AbsolutePath.StartsWith("/v1.0/admin/serviceAnnouncement/issues", StringComparison.OrdinalIgnoreCase);

    private static Uri? ReadNext(JsonElement root, out bool rejected)
    {
        rejected = false;
        if (!root.TryGetProperty("@odata.nextLink", out var link) || link.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(link.GetString()))
        {
            return null;
        }

        if (Uri.TryCreate(link.GetString(), UriKind.Absolute, out var u) && IsAllowedNext(u))
        {
            return u;
        }

        rejected = true;
        return null;
    }

    private static string BindHash(IssueQuery q)
    {
        var canonical = string.Join("|", q.Service, q.IsResolved, q.Classification, q.Status, q.StartFrom?.UtcTicks, q.StartTo?.UtcTicks, q.SearchText?.ToLowerInvariant(), q.MaxResults);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    private static string Version(string material) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();

    private static string SafePrefix(string value, int length) =>
        value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];

    private string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    private string Error(string code, string message, int? httpStatus = null) => Serialize(new
    {
        source = Source,
        queriedAtUtc = Now,
        status = "error",
        errors = new[] { new HealthError(code, message, httpStatus) },
        isPartial = false,
        truncated = false,
        hasMore = false
    });

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
