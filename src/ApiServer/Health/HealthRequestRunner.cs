using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ApiServer.Health;

/// <summary>Fehler einer Health-Abfrage mit stabilem Code (kein Token, keine URL mit Query im Text).</summary>
public sealed class HealthApiException(string code, string message, int? httpStatus = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
}

public sealed record PagedItems(List<JsonElement> Items, bool Truncated, string? TruncationReason);

/// <summary>
/// Gemeinsame HTTP-Ausfuehrung fuer Graph und Azure Resource Manager: Bearer-Token, Zeitlimit, begrenzte Wiederholung
/// (429 mit Retry-After, 502/503/504, Netzwerkfehler; Exponential Backoff mit Jitter), begrenzte Pagination mit Host-Allowlist
/// fuer nextLink. Es werden nie Tokens oder vollstaendige URLs geloggt.
/// </summary>
public class HealthRequestRunner
{
    public const string HttpClientName = "MicrosoftHealth";
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(3500)];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MicrosoftHealthOptions _options;
    private readonly ILogger<HealthRequestRunner> _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public HealthRequestRunner(
        IHttpClientFactory httpClientFactory,
        IOptions<MicrosoftHealthOptions> options,
        ILogger<HealthRequestRunner> logger,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
    }

    public virtual int MaxResultsLimit => _options.MaxResults;

    /// <summary>Holt ein JSON-Dokument (mit Wiederholung bei transienten Fehlern).</summary>
    public async Task<JsonDocument> GetJsonAsync(
        Uri uri, Func<CancellationToken, Task<string>> getToken, string source, CancellationToken cancellationToken)
    {
        var host = uri.Host;
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            HealthApiException failure;

            try
            {
                return await SendOnceAsync(uri, getToken, cancellationToken, headerRetryAfter => retryAfter = headerRetryAfter);
            }
            catch (HealthApiException ex) when (IsTransientCode(ex.Code))
            {
                failure = ex;
            }

            if (attempt > _retryDelays.Count)
            {
                _logger.LogError("Abfrage {Source} endgueltig fehlgeschlagen nach {Attempts} Versuchen (Host {Host}, Code {Code}, HTTP {Status}).",
                    source, attempt, host, failure.Code, failure.HttpStatus);
                throw failure;
            }

            var delay = retryAfter is { } fromHeader && fromHeader > TimeSpan.Zero
                ? (fromHeader > MaxRetryAfter ? MaxRetryAfter : fromHeader)
                : WithJitter(_retryDelays[attempt - 1]);

            _logger.LogWarning("Transienter Fehler bei {Source} (Versuch {Attempt}/{Max}, Host {Host}, Code {Code}, HTTP {Status}); neuer Versuch in {DelayMs} ms.",
                source, attempt, _retryDelays.Count + 1, host, failure.Code, failure.HttpStatus, (int)delay.TotalMilliseconds);
            await _delayAsync(delay, cancellationToken);
        }
    }

    /// <summary>
    /// Holt alle Seiten einer Collection (value-Array). <paramref name="keep"/> filtert clientseitig; <paramref name="maxItems"/>
    /// begrenzt die behaltenen Eintraege, <c>MaxPages</c> die Seitenanzahl. nextLink nur https auf erlaubten Hosts.
    /// </summary>
    public async Task<PagedItems> GetPagedAsync(
        Uri firstUri,
        Func<CancellationToken, Task<string>> getToken,
        IReadOnlySet<string> allowedHosts,
        string source,
        int maxItems,
        Func<JsonElement, bool>? keep,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var next = firstUri;
        var page = 0;

        while (true)
        {
            page++;
            using var document = await GetJsonAsync(next, getToken, source, cancellationToken);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                throw new HealthApiException("invalid_response", $"Unerwartete Antwortstruktur von {next.Host}: Feld 'value' (Array) fehlt.");
            }

            foreach (var element in value.EnumerateArray())
            {
                if (keep is not null && !keep(element))
                {
                    continue;
                }

                if (items.Count >= maxItems)
                {
                    return new PagedItems(items, true, $"maxResults ({maxItems}) erreicht; weitere Eintraege vorhanden.");
                }

                items.Add(element.Clone());
            }

            var nextLink = ReadNextLink(root);
            if (nextLink is null)
            {
                return new PagedItems(items, false, null);
            }

            if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var nextUri) ||
                nextUri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(nextUri.UserInfo) ||
                !nextUri.IsDefaultPort ||
                !allowedHosts.Contains(nextUri.Host.ToLowerInvariant()))
            {
                _logger.LogWarning("nextLink bei {Source} abgelehnt (Host nicht erlaubt oder ungueltig); Ergebnis wird abgeschnitten.", source);
                return new PagedItems(items, true, "nextLink wurde abgelehnt (Host/Schema nicht erlaubt); Ergebnis unvollstaendig.");
            }

            if (page >= _options.MaxPages)
            {
                return new PagedItems(items, true, $"MaxPages ({_options.MaxPages}) erreicht; weitere Seiten vorhanden.");
            }

            next = nextUri;
        }
    }

    private async Task<JsonDocument> SendOnceAsync(
        Uri uri, Func<CancellationToken, Task<string>> getToken, CancellationToken cancellationToken, Action<TimeSpan?> reportRetryAfter)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        try
        {
            string token;
            try
            {
                token = await getToken(timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Token-Beschaffung scheitert i. d. R. an Identitaet/Konfiguration (nicht transient). Keine Details (koennten Secrets enthalten).
                throw new HealthApiException("token_acquisition_failed",
                    $"Zugriffstoken fuer {uri.Host} konnte nicht beschafft werden ({ex.GetType().Name}). Identitaet/Konfiguration pruefen.", null, ex);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                try
                {
                    return JsonDocument.Parse(body);
                }
                catch (JsonException)
                {
                    throw new HealthApiException("invalid_response", $"Antwort von {uri.Host} ist kein gueltiges JSON.", (int)response.StatusCode);
                }
            }

            var status = (int)response.StatusCode;
            var detail = await TryReadErrorAsync(response, timeout.Token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                reportRetryAfter(ReadRetryAfter(response));
                throw new HealthApiException("throttled", $"{uri.Host} drosselt Anfragen (HTTP 429). {detail}".Trim(), status);
            }

            throw status switch
            {
                401 => new HealthApiException("unauthorized", $"{uri.Host} lehnt das Zugriffstoken ab (HTTP 401). {detail}".Trim(), status),
                403 => new HealthApiException("forbidden", $"Zugriff auf {uri.Host} verweigert (HTTP 403): erforderliche Berechtigung/Rolle fehlt. {detail}".Trim(), status),
                404 => new HealthApiException("not_found", $"Ressource bei {uri.Host} nicht gefunden (HTTP 404). {detail}".Trim(), status),
                409 => new HealthApiException("conflict", $"{uri.Host} meldet einen Konflikt (HTTP 409). {detail}".Trim(), status),
                400 => new HealthApiException("bad_request", $"{uri.Host} lehnt die Anfrage ab (HTTP 400). {detail}".Trim(), status),
                502 or 503 or 504 => new HealthApiException("upstream_unavailable", $"{uri.Host} voruebergehend nicht verfuegbar (HTTP {status}).", status),
                _ => new HealthApiException("upstream_error", $"{uri.Host} antwortete mit HTTP {status}. {detail}".Trim(), status)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HealthApiException("timeout", $"Zeitlimit ({_options.RequestTimeoutSeconds} s) bei {uri.Host} ueberschritten.");
        }
        catch (HttpRequestException ex)
        {
            throw new HealthApiException("network_error", $"Netzwerkfehler bei {uri.Host}: {ex.GetType().Name}.", ex.StatusCode is { } s ? (int)s : null, ex);
        }
        catch (IOException ex)
        {
            throw new HealthApiException("network_error", $"Netzwerkfehler bei {uri.Host}: {ex.GetType().Name}.", null, ex);
        }
    }

    private static bool IsTransientCode(string code) => code is "throttled" or "upstream_unavailable" or "network_error" or "timeout";

    private static string? ReadNextLink(JsonElement root)
    {
        foreach (var name in new[] { "@odata.nextLink", "nextLink" })
        {
            if (root.TryGetProperty(name, out var link) && link.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(link.GetString()))
            {
                return link.GetString();
            }
        }

        return null;
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static TimeSpan WithJitter(TimeSpan delay) =>
        delay + TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 0.2 * Random.Shared.NextDouble());

    /// <summary>
    /// Liest code/message best-effort (gekuerzt). Graph liefert {"error":{code,message}}, Azure Resource Health teils flach
    /// {"code","message"}. Antwort-Bodies enthalten keine Tokens.
    /// </summary>
    private static async Task<string> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var holder = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error : root;
            var code = holder.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var message = holder.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var text = string.Join(": ", new[] { code, message }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return text.Length > 300 ? text[..300] : text;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            // Body nicht auswertbar - ignorieren
        }

        return string.Empty;
    }
}
