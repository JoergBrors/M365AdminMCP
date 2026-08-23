using Microsoft.Extensions.Options;
using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;

namespace McpServer.Auth;

public class AzureAdOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ApiAppIdUri { get; set; } = string.Empty; // z.B. api://<api-server-app-id>
    public bool UseManagedIdentity { get; set; }
    public string? ManagedIdentityClientId { get; set; }
}

/// <summary>
/// Kapselt beide Token-Beschaffungswege gegen den ApiServer:
///  - App-only via Client Credentials Flow
///  - Delegated via On-Behalf-Of Flow (benötigt ein eingehendes User-Token)
/// </summary>
public class ApiTokenService
{
    private readonly AzureAdOptions _options;
    private readonly IConfidentialClientApplication? _app;
    private readonly TokenCredential? _managedIdentityCredential;

    public ApiTokenService(IOptions<AzureAdOptions> options)
    {
        _options = options.Value;

        if (_options.UseManagedIdentity)
        {
            _managedIdentityCredential = new ManagedIdentityCredential(
                string.IsNullOrWhiteSpace(_options.ManagedIdentityClientId)
                    ? ManagedIdentityId.SystemAssigned
                    : ManagedIdentityId.FromUserAssignedClientId(_options.ManagedIdentityClientId));
        }
        else
        {
            _app = ConfidentialClientApplicationBuilder
                .Create(_options.ClientId)
                .WithClientSecret(_options.ClientSecret)
                .WithAuthority($"https://login.microsoftonline.com/{_options.TenantId}")
                .Build();
        }
    }

    /// <summary>App-only Token (Client Credentials) – Audience = ApiServer, Claim "roles".</summary>
    public async Task<string> GetAppOnlyTokenAsync()
    {
        var scopes = new[] { $"{_options.ApiAppIdUri}/.default" };
        if (_managedIdentityCredential is not null)
        {
            var managedIdentityToken = await _managedIdentityCredential.GetTokenAsync(
                new TokenRequestContext(scopes),
                CancellationToken.None);
            return managedIdentityToken.Token;
        }

        var result = await _app!.AcquireTokenForClient(scopes).ExecuteAsync();
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
        if (_app is null)
        {
            throw new InvalidOperationException("On-Behalf-Of benoetigt die vertrauliche MCP-App mit ClientSecret. Managed Identity unterstuetzt diesen Flow nicht.");
        }

        var result = await _app.AcquireTokenOnBehalfOf(scopes, userAssertion).ExecuteAsync();
        return result.AccessToken;
    }
}
