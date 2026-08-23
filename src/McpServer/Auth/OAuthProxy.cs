using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace McpServer.Auth;

/// <summary>
/// Duenne OAuth-Authorization-Server-Fassade vor Entra ID.
///
/// Hintergrund: Der MCP-Autorisierungs-Standard (RFC 8707/9728) verlangt, dass Clients (z.B.
/// Claude) einen "resource"-Parameter mit der kanonischen MCP-Server-URL an /authorize und
/// /token senden. Entra ID implementiert RFC 8707 nicht - sein v2.0-Endpunkt leitet die
/// Ziel-Audience ausschliesslich aus "scope" ab und lehnt jeden abweichenden "resource"-Wert
/// mit AADSTS9010010 ab. Diese Fassade akzeptiert den RFC-8707-konformen Request des Clients,
/// entfernt NUR den "resource"-Parameter und leitet alles andere (PKCE, state, scope,
/// redirect_uri, client_id) unveraendert an Entra weiter - eine reine "strip one param"-Pipe,
/// kein eigener Token-Aussteller. PKCE bleibt vollstaendig End-zu-Ende zwischen Client und
/// Entra bestehen; diese Fassade sieht niemals ein Client Secret (alle MCP-OAuth-Clients sind
/// public clients, siehe terraform/modules/entra-id/main.tf).
///
/// Quellen: https://github.com/anthropics/claude-code/issues/76096,
/// https://www.groff.dev/blog/azure-entra-id-mcp-server-authentication-incompatibilities
/// </summary>
public static class OAuthProxy
{
    private static readonly string[] ResourceParamNames = ["resource"];

    public static void MapOAuthProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/authorize", HandleAuthorize).AllowAnonymous();
        app.MapPost("/token", HandleToken).AllowAnonymous();
        app.MapGet("/.well-known/oauth-authorization-server", HandleAuthorizationServerMetadata).AllowAnonymous();
        app.MapGet("/.well-known/oauth-authorization-server/mcp", HandleAuthorizationServerMetadata).AllowAnonymous();
    }

    private static IResult HandleAuthorize(
        HttpContext context,
        IOptions<AzureAdOptions> azureAdOptions,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("OAuthProxy");
        var tenantId = azureAdOptions.Value.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Results.Problem("AzureAd:TenantId ist nicht konfiguriert.", statusCode: StatusCodes.Status500InternalServerError);
        }

        var redirectUri = context.Request.Query["redirect_uri"].ToString();
        if (!IsRedirectUriAllowed(redirectUri, configuration))
        {
            logger.LogWarning("OAuthProxy /authorize: redirect_uri '{RedirectUri}' nicht in der Allowlist - Anfrage abgelehnt.", redirectUri);
            return Results.BadRequest(new { error = "invalid_request", error_description = "redirect_uri is not in the allowed list." });
        }

        var query = QueryHelpers.ParseNullableQuery(context.Request.QueryString.Value);
        var forwarded = new Dictionary<string, StringValues>(query is null ? [] : query);
        foreach (var name in ResourceParamNames)
        {
            forwarded.Remove(name);
        }

        var entraAuthorizeUrl = QueryHelpers.AddQueryString(
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize",
            forwarded.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString()));

        return Results.Redirect(entraAuthorizeUrl);
    }

    private static async Task<IResult> HandleToken(
        HttpContext context,
        IOptions<AzureAdOptions> azureAdOptions,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("OAuthProxy");
        var tenantId = azureAdOptions.Value.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Results.Problem("AzureAd:TenantId ist nicht konfiguriert.", statusCode: StatusCodes.Status500InternalServerError);
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "invalid_request", error_description = "Expected application/x-www-form-urlencoded body." });
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var forwarded = new Dictionary<string, string>();
        foreach (var pair in form)
        {
            if (ResourceParamNames.Contains(pair.Key)) { continue; }
            forwarded[pair.Key] = pair.Value.ToString();
        }

        var client = httpClientFactory.CreateClient();
        using var forwardedContent = new FormUrlEncodedContent(forwarded);
        using var upstreamResponse = await client.PostAsync(
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            forwardedContent,
            context.RequestAborted);

        var body = await upstreamResponse.Content.ReadAsStringAsync(context.RequestAborted);
        var contentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";

        if (!upstreamResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("OAuthProxy /token: Entra antwortete mit {StatusCode}.", (int)upstreamResponse.StatusCode);
        }

        return Results.Text(body, contentType, statusCode: (int)upstreamResponse.StatusCode);
    }

    private static async Task<IResult> HandleAuthorizationServerMetadata(
        HttpContext context,
        IOptions<AzureAdOptions> azureAdOptions,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache)
    {
        var tenantId = azureAdOptions.Value.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Results.Problem("AzureAd:TenantId ist nicht konfiguriert.", statusCode: StatusCodes.Status500InternalServerError);
        }

        var baseUrl = GetExternalBaseUrl(context, configuration);

        var entraMetadata = await cache.GetOrCreateAsync($"entra-oidc-metadata-{tenantId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            var client = httpClientFactory.CreateClient();
            var response = await client.GetAsync(
                $"https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration",
                context.RequestAborted);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(context.RequestAborted);
            return JsonDocument.Parse(json);
        });

        var root = entraMetadata!.RootElement;
        SetNoStoreHeaders(context.Response);

        var metadata = new Dictionary<string, object?>
        {
            ["issuer"] = $"https://login.microsoftonline.com/{tenantId}/v2.0",
            ["authorization_endpoint"] = $"{baseUrl}/authorize",
            ["token_endpoint"] = $"{baseUrl}/token",
            ["jwks_uri"] = root.GetProperty("jwks_uri").GetString(),
            ["response_types_supported"] = ReadStringArray(root, "response_types_supported"),
            ["subject_types_supported"] = ReadStringArray(root, "subject_types_supported"),
            ["id_token_signing_alg_values_supported"] = ReadStringArray(root, "id_token_signing_alg_values_supported"),
            ["code_challenge_methods_supported"] = ReadStringArray(root, "code_challenge_methods_supported") ?? ["S256"],
            ["grant_types_supported"] = ReadStringArray(root, "grant_types_supported") ?? ["authorization_code", "refresh_token"],
            ["scopes_supported"] = ReadStringArray(root, "scopes_supported"),
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
        };

        return Results.Json(metadata);
    }

    private static string[]? ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return element.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
    }

    private static bool IsRedirectUriAllowed(string redirectUri, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (uri.Scheme != "https")
        {
            return false;
        }

        var allowedHosts = configuration.GetSection("McpAuth:AllowedRedirectHosts").Get<string[]>();
        if (allowedHosts is null || allowedHosts.Length == 0)
        {
            // Kein explizites Allowlisting konfiguriert: Entra selbst validiert redirect_uri
            // strikt gegen die je Client-App registrierten Redirect-URIs (siehe
            // terraform/modules/entra-id/main.tf) - ein hier nicht erlaubter Wert wird von
            // Entra ohnehin abgelehnt, bevor ein Code ausgestellt wird.
            return true;
        }

        return allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetExternalBaseUrl(HttpContext context, IConfiguration configuration)
    {
        var configuredBaseUrl = configuration["McpAuth:ExternalBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return configuredBaseUrl.TrimEnd('/');
        }
        return $"{context.Request.Scheme}://{context.Request.Host}".TrimEnd('/');
    }

    private static void SetNoStoreHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }
}
