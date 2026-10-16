using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace ApiServer.Auth;

/// <summary>
/// Token fuer Azure Resource Manager (https://management.azure.com/.default). Nutzt dieselbe Identitaetskonfiguration wie
/// <see cref="GraphTokenService"/> (GraphAuth:UseManagedIdentity bzw. lokal AzureAd:ClientSecret), also dieselbe Runtime-Identitaet.
/// </summary>
public class ArmTokenService
{
    private static readonly string[] ArmScopes = ["https://management.azure.com/.default"];

    private readonly TokenCredential? _managedIdentityCredential;
    private readonly IConfidentialClientApplication? _confidentialClient;

    public ArmTokenService(IConfiguration configuration, IOptions<GraphAuthOptions> options)
    {
        var graphAuth = options.Value;

        if (graphAuth.UseManagedIdentity)
        {
            _managedIdentityCredential = new ManagedIdentityCredential(
                string.IsNullOrWhiteSpace(graphAuth.ManagedIdentityClientId)
                    ? ManagedIdentityId.SystemAssigned
                    : ManagedIdentityId.FromUserAssignedClientId(graphAuth.ManagedIdentityClientId));
            return;
        }

        var tenantId = configuration["AzureAd:TenantId"];
        var clientId = configuration["AzureAd:ClientId"];
        var clientSecret = configuration["AzureAd:ClientSecret"];

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "ARM-Authentifizierung ist nicht konfiguriert. Setze in Azure GraphAuth:UseManagedIdentity=true oder lokal AzureAd:ClientSecret fuer die API-App.");
        }

        _confidentialClient = ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .Build();
    }

    public virtual async Task<string> GetArmTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_managedIdentityCredential is not null)
        {
            var token = await _managedIdentityCredential.GetTokenAsync(new TokenRequestContext(ArmScopes), cancellationToken);
            return token.Token;
        }

        var result = await _confidentialClient!.AcquireTokenForClient(ArmScopes).ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }
}
