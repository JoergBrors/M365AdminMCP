using System.Security.Claims;
using System.Text.RegularExpressions;

namespace McpServer.Auth;

/// <summary>
/// Validierte Nutzeridentitaet des MCP-Aufrufers, ausschliesslich fuer AUDIT/Nachvollziehbarkeit (Entscheidung D-1/D-2, ADR-0006).
///
/// Sicherheitsregeln:
///  - Der Kontext wird NUR aus dem bereits validierten JWT des McpServers gebaut (<see cref="FromPrincipal"/>), nie aus Client-Headern
///    oder Tool-Parametern.
///  - Er ist KEINE Autorisierungsgrundlage. Die API entscheidet allein anhand des App-only-Tokens der MCP-Identitaet (App-Rolle).
///  - Beim Lesen (<see cref="FromHeaders"/>) wird jeder Wert strikt validiert; ungueltige Werte werden verworfen.
///  - Eine Verbindung ueber den statischen API-Key (ADR-0014) hat keine Nutzeridentitaet und wird als solche markiert.
/// Die Datei wird von McpServer und ApiServer genutzt (Compile-Link, ADR-0009), damit beide Seiten identisch validieren.
/// </summary>
public sealed partial record UserContext(string AuthMethod, string? Oid, string? Tid, string? PreferredUsername)
{
    public const string OidHeader = "X-MCP-User-Oid";
    public const string TidHeader = "X-MCP-User-Tid";
    public const string UpnHeader = "X-MCP-User-Upn";
    public const string MethodHeader = "X-MCP-Auth-Method";

    public const string MethodEntra = "entra";
    public const string MethodApiKey = "apikey";
    public const string MethodNone = "none";

    private const int MaxUsernameLength = 256;

    public static readonly UserContext Anonymous = new(MethodNone, null, null, null);

    [GeneratedRegex(@"^[A-Za-z0-9._%+@'\-]{1,256}$")]
    private static partial Regex UsernamePattern();

    public static UserContext FromPrincipal(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return Anonymous;
        }

        if (string.Equals(user.Identity.AuthenticationType, "McpApiKey", StringComparison.Ordinal))
        {
            return new UserContext(MethodApiKey, null, null, null);
        }

        var oid = CleanGuid(First(user, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"));
        var tid = CleanGuid(First(user, "tid", "http://schemas.microsoft.com/identity/claims/tenantid"));
        var upn = CleanUsername(First(user, "preferred_username"));
        return oid is null && tid is null && upn is null
            ? Anonymous
            : new UserContext(MethodEntra, oid, tid, upn);
    }

    public IEnumerable<KeyValuePair<string, string>> ToHeaders()
    {
        yield return new(MethodHeader, AuthMethod);
        if (Oid is not null) yield return new(OidHeader, Oid);
        if (Tid is not null) yield return new(TidHeader, Tid);
        if (PreferredUsername is not null) yield return new(UpnHeader, PreferredUsername);
    }

    public static UserContext FromHeaders(IHeaderDictionary headers)
    {
        var method = headers[MethodHeader].ToString();
        if (method is not (MethodEntra or MethodApiKey))
        {
            return Anonymous;
        }

        return new UserContext(
            method,
            CleanGuid(headers[OidHeader].ToString()),
            CleanGuid(headers[TidHeader].ToString()),
            CleanUsername(headers[UpnHeader].ToString()));
    }

    private static string? First(ClaimsPrincipal user, params string[] types) =>
        types.Select(t => user.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? CleanGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var guid) ? guid.ToString("D") : null;

    private static string? CleanUsername(string? value) =>
        value is { Length: > 0 and <= MaxUsernameLength } && UsernamePattern().IsMatch(value) ? value : null;
}
