using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace McpServer.Auth;

public class AzureAdOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string ApiAppIdUri { get; set; } = string.Empty; // z.B. api://<api-server-app-id>
}

/// <summary>
/// Kapselt beide Token-Beschaffungswege gegen den ApiServer:
///  - App-only via Client Credentials Flow
///  - Delegated via On-Behalf-Of Flow (benötigt ein eingehendes User-Token)
/// </summary>
public class ApiTokenService
{
    private readonly AzureAdOptions _options;
    private readonly IConfidentialClientApplication _app;

    public ApiTokenService(IOptions<AzureAdOptions> options)
    {
        _options = options.Value;

        _app = ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithClientSecret(_options.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{_options.TenantId}")
            .Build();
    }

    /// <summary>App-only Token (Client Credentials) – Audience = ApiServer, Claim "roles".</summary>
    public async Task<string> GetAppOnlyTokenAsync()
    {
        var scopes = new[] { $"{_options.ApiAppIdUri}/.default" };
        var result = await _app.AcquireTokenForClient(scopes).ExecuteAsync();
        return result.AccessToken;
    }

    /// <summary>
    /// Delegated Token via On-Behalf-Of: tauscht das eingehende User-Token (Audience = mcp-server)
    /// gegen ein neues Token (Audience = api-server, Claim "scp") – der Nutzerkontext bleibt erhalten.
    /// </summary>
    public async Task<string> GetOnBehalfOfTokenAsync(string incomingUserAccessToken)
    {
        var scopes = new[] { $"{_options.ApiAppIdUri}/Tasks.ReadWrite" };
        var userAssertion = new UserAssertion(incomingUserAccessToken);
        var result = await _app.AcquireTokenOnBehalfOf(scopes, userAssertion).ExecuteAsync();
        return result.AccessToken;
    }

    /// <summary>
    /// App-only Token für Microsoft Graph (Tenant-weite Office 365 Status-, Message-Center-
    /// und Usage-Report-Abfragen). Benötigt die Application Permissions
    /// ServiceHealth.Read.All, ServiceMessage.Read.All, Reports.Read.All mit Admin Consent
    /// auf der mcp-server App-Registrierung (siehe entra-setup.sh bzw. terraform/modules/entra-id).
    /// </summary>
    public async Task<string> GetGraphAppOnlyTokenAsync()
    {
        var scopes = new[] { "https://graph.microsoft.com/.default" };
        var result = await _app.AcquireTokenForClient(scopes).ExecuteAsync();
        return result.AccessToken;
    }
}
