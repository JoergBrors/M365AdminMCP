using System.Net.Http.Headers;
using McpServer.Auth;

namespace McpServer.Services;

public class ApiServerClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApiTokenService _tokenService;
    private readonly IConfiguration _configuration;

    public ApiServerClient(
        IHttpClientFactory httpClientFactory,
        ApiTokenService tokenService,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
        _configuration = configuration;
    }

    public async Task<string> GetAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var token = await _tokenService.GetAppOnlyTokenAsync();
        return await GetAsync(relativePath, token, cancellationToken);
    }

    public async Task<string> GetAsync(string relativePath, string bearerToken, CancellationToken cancellationToken = default)
    {
        var client = CreateClient(bearerToken);
        var response = await client.GetAsync(relativePath, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private HttpClient CreateClient(string bearerToken)
    {
        var apiBaseUrl = _configuration["ApiServer:BaseUrl"]
            ?? throw new InvalidOperationException("ApiServer:BaseUrl ist nicht konfiguriert.");

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(apiBaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }
}
