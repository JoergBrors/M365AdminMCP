using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace ApiServer.Auth;

public class GraphTokenService
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly IConfiguration _configuration;
    private readonly GraphAuthOptions _options;
    private readonly TokenCredential? _managedIdentityCredential;
    private readonly IConfidentialClientApplication? _confidentialClient;

    public GraphTokenService(IConfiguration configuration, IOptions<GraphAuthOptions> options)
    {
        _configuration = configuration;
        _options = options.Value;

        if (_options.UseManagedIdentity)
        {
            _managedIdentityCredential = string.IsNullOrWhiteSpace(_options.ManagedIdentityClientId)
                ? new ManagedIdentityCredential()
                : new ManagedIdentityCredential(_options.ManagedIdentityClientId);
            return;
        }

        var tenantId = _configuration["AzureAd:TenantId"];
        var clientId = _configuration["AzureAd:ClientId"];
        var clientSecret = _configuration["AzureAd:ClientSecret"];

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "GraphAuth ist nicht konfiguriert. Setze in Azure GraphAuth:UseManagedIdentity=true oder lokal AzureAd:ClientSecret fuer die API-App.");
        }

        _confidentialClient = ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .Build();
    }

    public async Task<string> GetGraphTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_managedIdentityCredential is not null)
        {
            var token = await _managedIdentityCredential.GetTokenAsync(
                new TokenRequestContext(GraphScopes),
                cancellationToken);
            return token.Token;
        }

        var result = await _confidentialClient!.AcquireTokenForClient(GraphScopes).ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }
}
