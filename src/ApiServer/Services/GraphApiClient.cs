using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ApiServer.Auth;
using McpServer.Reporting;
using McpServer.Utils;

namespace ApiServer.Services;

public class GraphApiClient
{
    // Begrenzte Wiederholung nur fuer transiente Fehler (siehe IsTransient): Versuche = Wartezeiten + 1.
    private static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500)];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GraphTokenService _tokenService;
    private readonly ILogger<GraphApiClient> _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public GraphApiClient(
        IHttpClientFactory httpClientFactory,
        GraphTokenService tokenService,
        ILogger<GraphApiClient> logger,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
        _logger = logger;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    private int MaxAttempts => _retryDelays.Count + 1;

    public async Task<string> GetServiceHealthOverviewAsync(CancellationToken cancellationToken = default)
    {
        return await GetGraphV1Async("/admin/serviceAnnouncement/healthOverviews", cancellationToken);
    }

    public async Task<string> GetServiceHealthDetailAsync(
        string serviceHealthId,
        bool expandIssues = true,
        bool includeResolvedIssues = false,
        CancellationToken cancellationToken = default)
    {
        var path = $"/admin/serviceAnnouncement/healthOverviews/{Uri.EscapeDataString(serviceHealthId)}";

        try
        {
            var directPath = expandIssues ? $"{path}?$expand=issues" : path;
            return await GetGraphV1Async(directPath, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return await GetServiceHealthDetailFallbackAsync(
                serviceHealthId,
                expandIssues,
                includeResolvedIssues,
                ex.StatusCode,
                cancellationToken);
        }
    }

    public async Task<string> GetDegradedServiceHealthDetailsAsync(CancellationToken cancellationToken = default)
    {
        var overviewJson = await GetServiceHealthOverviewAsync(cancellationToken);
        using var overview = JsonDocument.Parse(overviewJson);

        var degradedServiceIds = overview.RootElement
            .GetProperty("value")
            .EnumerateArray()
            .Where(service =>
                service.TryGetProperty("status", out var status) &&
                !string.Equals(status.GetString(), "ServiceOperational", StringComparison.OrdinalIgnoreCase))
            .Select(service => service.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();

        var details = new List<JsonElement>();
        foreach (var serviceId in degradedServiceIds)
        {
            var detailJson = await GetServiceHealthDetailAsync(
                serviceId,
                expandIssues: true,
                includeResolvedIssues: false,
                cancellationToken);
            using var detail = JsonDocument.Parse(detailJson);
            details.Add(detail.RootElement.Clone());
        }

        return JsonSerializer.Serialize(new
        {
            count = details.Count,
            value = details
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> GetServiceHealthIssuesAsync(CancellationToken cancellationToken = default)
    {
        return await GetGraphV1Async("/admin/serviceAnnouncement/issues", cancellationToken);
    }

    public async Task<string> GetMessageCenterMessagesAsync(string? odataFilter, CancellationToken cancellationToken = default)
    {
        var path = "/admin/serviceAnnouncement/messages";
        if (!string.IsNullOrWhiteSpace(odataFilter))
        {
            path += $"?$filter={Uri.EscapeDataString(odataFilter)}";
        }

        return await GetGraphV1Async(path, cancellationToken);
    }

    private async Task<string> GetServiceHealthDetailFallbackAsync(
        string serviceHealthId,
        bool expandIssues,
        bool includeResolvedIssues,
        HttpStatusCode? directEndpointStatusCode,
        CancellationToken cancellationToken)
    {
        var overviewJson = await GetServiceHealthOverviewAsync(cancellationToken);
        using var overview = JsonDocument.Parse(overviewJson);

        var service = overview.RootElement
            .GetProperty("value")
            .EnumerateArray()
            .FirstOrDefault(item =>
                item.TryGetProperty("id", out var id) &&
                string.Equals(id.GetString(), serviceHealthId, StringComparison.OrdinalIgnoreCase));

        if (service.ValueKind == JsonValueKind.Undefined)
        {
            throw new KeyNotFoundException($"ServiceHealthId '{serviceHealthId}' wurde in der Health Overview nicht gefunden.");
        }

        var serviceClone = service.Clone();
        var serviceName = service.TryGetProperty("service", out var serviceProperty)
            ? serviceProperty.GetString()
            : serviceHealthId;

        JsonElement[] issues = [];
        if (expandIssues && !string.IsNullOrWhiteSpace(serviceName))
        {
            var filter = $"service eq '{EscapeODataString(serviceName)}'";
            if (!includeResolvedIssues)
            {
                filter += " and isResolved eq false";
            }

            var issuesJson = await GetGraphV1Async(
                $"/admin/serviceAnnouncement/issues?$filter={Uri.EscapeDataString(filter)}",
                cancellationToken);

            using var issuesDocument = JsonDocument.Parse(issuesJson);
            issues = issuesDocument.RootElement
                .GetProperty("value")
                .EnumerateArray()
                .Select(issue => issue.Clone())
                .ToArray();
        }

        return JsonSerializer.Serialize(new
        {
            source = "microsoftGraph.healthOverviews plus filtered serviceAnnouncement.issues",
            directMicrosoftGraphEndpoint = $"/admin/serviceAnnouncement/healthOverviews/{serviceHealthId}",
            directMicrosoftGraphStatus = directEndpointStatusCode?.ToString(),
            includeResolvedIssues,
            issueCount = issues.Length,
            healthOverview = serviceClone,
            issues
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> GetReportAsJsonAsync(string reportPathWithoutQuery, CancellationToken cancellationToken = default)
    {
        var token = await _tokenService.GetGraphTokenAsync(cancellationToken);

        var noRedirectClient = _httpClientFactory.CreateClient("GraphNoRedirect");
        noRedirectClient.BaseAddress = new Uri("https://graph.microsoft.com/");
        noRedirectClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var separator = reportPathWithoutQuery.Contains('?') ? "&" : "?";
        var jsonAttemptUrl = $"beta/{TrimLeadingSlash(reportPathWithoutQuery)}{separator}$format=application/json";

        var response = await SendGraphAsync(noRedirectClient, jsonAttemptUrl, reportPathWithoutQuery, cancellationToken);

        // Einzelne Reports (z.B. getOffice365ActiveUserCounts) liefern mit $format=application/json einen Graph-seitigen
        // 400 ("UnknownError"), obwohl der Standardweg ohne $format (302 auf CSV-Download) funktioniert. Einmal ohne
        // $format wiederholen; schlaegt auch das fehl, bleibt die urspruengliche Antwort fuer die Fehlermeldung erhalten.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var plainResponse = await SendGraphAsync(
                noRedirectClient, $"beta/{TrimLeadingSlash(reportPathWithoutQuery)}", reportPathWithoutQuery, cancellationToken);
            if (plainResponse.IsSuccessStatusCode || (int)plainResponse.StatusCode is 301 or 302 or 307 or 308)
            {
                response.Dispose();
                response = plainResponse;
            }
            else
            {
                plainResponse.Dispose();
            }
        }

        if (response.IsSuccessStatusCode &&
            response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is not null)
        {
            var csv = await DownloadCsvAsync(response.Headers.Location, reportPathWithoutQuery, cancellationToken);

            return CsvJsonConverter.ConvertToJsonArray(csv);
        }

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return CsvJsonConverter.ConvertToJsonArray(body);
        }

        // Graph-Fehlertext mitgeben (enthaelt keine Tokens), sonst bleibt nur "400 (Bad Request)" ohne Ursache.
        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (errorBody.Length > 600)
        {
            errorBody = errorBody[..600];
        }

        throw new HttpRequestException(
            $"Microsoft Graph antwortete mit {(int)response.StatusCode} ({response.StatusCode}) fuer '{reportPathWithoutQuery}': {errorBody}",
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>GET gegen Graph; transiente Statuscodes (408/429/502/503/504) und Netzwerkfehler werden begrenzt wiederholt.</summary>
    private Task<HttpResponseMessage> SendGraphAsync(
        HttpClient client, string relativeUrl, string reportPath, CancellationToken cancellationToken)
    {
        return RetryAsync("graph-report", client.BaseAddress?.Host ?? "graph", reportPath, async () =>
        {
            var response = await client.GetAsync(relativeUrl, cancellationToken);
            if (IsTransientStatus(response.StatusCode))
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Microsoft Graph antwortete mit {(int)status} ({status}).", null, status);
            }

            return response;
        }, cancellationToken);
    }

    /// <summary>
    /// Laedt die vorautorisierte CSV-URL des Report-Dienstes. Es wird bewusst KEIN Authorization-Header gesendet; die URL
    /// (enthaelt ein kurzlebiges Token in der Query) wird nie geloggt, nur Host und Report-Pfad.
    /// </summary>
    private async Task<string> DownloadCsvAsync(Uri location, string reportPath, CancellationToken cancellationToken)
    {
        var downloadClient = _httpClientFactory.CreateClient();
        downloadClient.DefaultRequestHeaders.Authorization = null;
        var attempts = 0;

        try
        {
            return await RetryAsync("csv-download", location.Host, reportPath, async () =>
            {
                attempts++;
                using var request = new HttpRequestMessage(HttpMethod.Get, location);
                request.Headers.Authorization = null;
                using var response = await downloadClient.SendAsync(request, cancellationToken);
                if (IsTransientStatus(response.StatusCode))
                {
                    throw new HttpRequestException(
                        $"Download antwortete mit {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Der Download-Host (reports*.office.com) ist ein separater Microsoft-Dienst. Fuer einzelne Reports (z.B.
            // getOffice365ActiveUserCounts) schliesst er die Verbindung nach dem TLS-Aufbau reproduzierbar ohne Antwort.
            throw new HttpRequestException(
                $"Der CSV-Download des Reports '{reportPath}' von Microsoft ({location.Host}) ist nach {attempts} Versuch(en) fehlgeschlagen " +
                $"({Describe(ex).Phase}): {ex.Message}",
                ex,
                ex.StatusCode);
        }
    }

    private async Task<T> RetryAsync<T>(
        string operation, string host, string reportPath, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                var failure = Describe(ex);
                if (attempt >= MaxAttempts)
                {
                    _logger.LogError(
                        "Transienter Fehler bei {Operation} endgueltig nach {Attempts} Versuchen (Host {Host}, Report {Report}, Phase {Phase}, SocketError {SocketError}, HttpRequestError {HttpRequestError}).",
                        operation, attempt, host, reportPath, failure.Phase, failure.SocketError, failure.HttpRequestError);
                    throw;
                }

                var baseDelay = _retryDelays[attempt - 1];
                var delay = baseDelay + TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * 0.2 * Random.Shared.NextDouble());
                _logger.LogWarning(
                    "Transienter Fehler bei {Operation} (Versuch {Attempt}/{MaxAttempts}, Host {Host}, Report {Report}, Phase {Phase}, SocketError {SocketError}, HttpRequestError {HttpRequestError}); neuer Versuch in {DelayMs} ms.",
                    operation, attempt, MaxAttempts, host, reportPath, failure.Phase, failure.SocketError, failure.HttpRequestError, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Nur Netzwerk-/Timeout-Fehler und die Statuscodes 408/429/502/503/504 gelten als transient. 4xx (ausser 408/429),
    /// Validierungsfehler und vom Aufrufer ausgeloeste Abbrueche werden nie wiederholt.
    /// </summary>
    private static bool IsTransient(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return ex switch
        {
            HttpRequestException { StatusCode: { } status } => IsTransientStatus(status),
            HttpRequestException hre => IsConnectionLevel(hre.HttpRequestError) || HasNetworkInner(hre),
            TaskCanceledException => true, // HttpClient-Timeout (Aufrufer-Abbruch ist oben ausgeschlossen)
            IOException => true,
            _ => false
        };
    }

    private static bool IsConnectionLevel(HttpRequestError error) =>
        error is HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
            or HttpRequestError.NameResolutionError or HttpRequestError.ProxyTunnelError;

    private static bool HasNetworkInner(Exception ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static (string Phase, SocketError? SocketError, HttpRequestError? HttpRequestError) Describe(Exception ex)
    {
        SocketError? socketError = null;
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException se)
            {
                socketError = se.SocketErrorCode;
            }
        }

        var requestError = (ex as HttpRequestException)?.HttpRequestError;
        var phase = ex switch
        {
            HttpRequestException { StatusCode: { } status } => $"HTTP-Status {(int)status}",
            TaskCanceledException => "Timeout",
            HttpRequestException hre when IsConnectionLevel(hre.HttpRequestError) => "Verbindungsaufbau/TLS",
            _ when socketError is not null || HasNetworkInner(ex) => "nach Verbindungsaufbau: Verbindung ohne Antwort beendet",
            _ => "unbekannt"
        };

        return (phase, socketError, requestError);
    }

    public Task<string> GetCatalogReportAsJsonAsync(
        string functionName,
        string? period = null,
        string? date = null,
        string? serviceArea = null,
        string? appId = null,
        CancellationToken cancellationToken = default)
    {
        if (!ReportCatalog.Reports.TryGetValue(functionName, out var def))
        {
            throw new ArgumentException(
                $"Unbekannter Report '{functionName}'. Nutze zuerst den Reports-Katalog.");
        }

        string path;

        switch (def.ParamMode)
        {
            case ReportParamMode.PeriodOrDate:
                if (!string.IsNullOrWhiteSpace(period) && !string.IsNullOrWhiteSpace(date))
                {
                    throw new ArgumentException($"'{functionName}' akzeptiert entweder period ODER date, nicht beides.");
                }
                if (!string.IsNullOrWhiteSpace(date))
                {
                    path = $"/reports/{def.FunctionName}(date={date})";
                }
                else
                {
                    var p = ValidatePeriod(period ?? ReportCatalog.GetDefaultPeriod(def), def);
                    path = $"/reports/{def.FunctionName}(period='{p}')";
                }
                break;

            case ReportParamMode.PeriodOnly:
                var periodOnly = ValidatePeriod(period ?? ReportCatalog.GetDefaultPeriod(def), def);
                path = $"/reports/{def.FunctionName}(period='{periodOnly}')";
                break;

            case ReportParamMode.None:
                path = $"/reports/{def.FunctionName}";
                break;

            case ReportParamMode.Special when def.FunctionName == "getApiUsage":
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(period)) parts.Add($"period='{ValidatePeriod(period, def)}'");
                if (!string.IsNullOrWhiteSpace(serviceArea)) parts.Add($"serviceArea='{serviceArea}'");
                if (!string.IsNullOrWhiteSpace(appId)) parts.Add($"appId='{appId}'");
                path = parts.Count > 0 ? $"/reports/{def.FunctionName}({string.Join(",", parts)})" : $"/reports/{def.FunctionName}";
                break;

            default:
                throw new NotSupportedException($"ParamMode '{def.ParamMode}' fuer '{functionName}' ist nicht implementiert.");
        }

        return GetReportAsJsonAsync(path, cancellationToken);
    }

    private async Task<string> GetGraphV1Async(string relativePath, CancellationToken cancellationToken)
    {
        var token = await _tokenService.GetGraphTokenAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://graph.microsoft.com/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"v1.0/{TrimLeadingSlash(relativePath)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string TrimLeadingSlash(string path)
    {
        return path.TrimStart('/');
    }

    private static string EscapeODataString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string ValidatePeriod(string period, ReportDefinition definition)
    {
        var allowedPeriods = ReportCatalog.GetAllowedPeriods(definition);
        if (!allowedPeriods.Contains(period, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Ungueltiger period-Wert '{period}' fuer '{definition.FunctionName}'. Erlaubt: {string.Join(", ", allowedPeriods)}.");
        }

        return period.ToUpperInvariant();
    }
}
