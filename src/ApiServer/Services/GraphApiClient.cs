using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using ApiServer.Auth;
using McpServer.Reporting;
using McpServer.Utils;

namespace ApiServer.Services;

public class GraphApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GraphTokenService _tokenService;

    public GraphApiClient(IHttpClientFactory httpClientFactory, GraphTokenService tokenService)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
    }

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

        var response = await noRedirectClient.GetAsync(jsonAttemptUrl, cancellationToken);

        if (response.IsSuccessStatusCode &&
            response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is not null)
        {
            var downloadClient = _httpClientFactory.CreateClient();
            var csv = await downloadClient.GetStringAsync(response.Headers.Location, cancellationToken);
            return CsvJsonConverter.ConvertToJsonArray(csv);
        }

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return CsvJsonConverter.ConvertToJsonArray(body);
        }

        response.EnsureSuccessStatusCode();
        return string.Empty;
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
                    var p = ValidatePeriod(period ?? "D30", functionName);
                    path = $"/reports/{def.FunctionName}(period='{p}')";
                }
                break;

            case ReportParamMode.PeriodOnly:
                var periodOnly = ValidatePeriod(period ?? "D30", functionName);
                path = $"/reports/{def.FunctionName}(period='{periodOnly}')";
                break;

            case ReportParamMode.None:
                path = $"/reports/{def.FunctionName}";
                break;

            case ReportParamMode.Special when def.FunctionName == "getApiUsage":
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(period)) parts.Add($"period='{ValidatePeriod(period, functionName)}'");
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

    private static string ValidatePeriod(string period, string functionName)
    {
        if (!ReportCatalog.AllowedPeriods.Contains(period, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Ungueltiger period-Wert '{period}' fuer '{functionName}'. Erlaubt: {string.Join(", ", ReportCatalog.AllowedPeriods)}.");
        }

        return period.ToUpperInvariant();
    }
}
