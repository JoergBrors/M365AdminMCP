using System.Globalization;
using System.Text.RegularExpressions;

namespace McpServer.Utils;

/// <summary>
/// Eingabevalidierung fuer die Microsoft-Health-Tools (Intune, Message Center, Azure Resource Health).
/// Wird im McpServer (Tool-Ebene, damit das LLM eine lesbare Fehlermeldung bekommt) und im ApiServer
/// (verbindliche Pruefung, per Compile-Link, siehe ADR-0009) verwendet. Es gibt bewusst nur eine Implementierung.
/// </summary>
public static partial class HealthInputValidator
{
    public const int DefaultMaxResults = 100;
    public const int HardMaxResults = 1000;
    private const int MaxResourceIdLength = 1024;

    // Erlaubte Zeichen je Pfadsegment eines Azure-Resource-IDs. Kein '%', '?', '#', '\', ':', '/' oder Leerzeichen.
    [GeneratedRegex(@"^[A-Za-z0-9._()~\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentRegex();

    /// <summary>Normalisiert eine Subscription-ID (GUID im Format "D") auf Kleinbuchstaben.</summary>
    public static bool TryParseSubscriptionId(string? value, out string subscriptionId, out string error)
    {
        subscriptionId = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParseExact(value.Trim(), "D", out var guid) || guid == Guid.Empty)
        {
            error = "subscriptionId muss eine gueltige GUID sein (Format 00000000-0000-0000-0000-000000000000).";
            return false;
        }

        subscriptionId = guid.ToString("D");
        return true;
    }

    /// <summary>
    /// Validiert eine vollstaendige Azure-Resource-ID
    /// (/subscriptions/{guid}/resourceGroups/{rg}/providers/{namespace}/{type}/{name}[/{type}/{name}]...).
    /// Vollstaendige URLs, Hosts, Query-/Fragment-Anteile, Traversal und kodierte Zeichen werden abgelehnt.
    /// </summary>
    public static bool TryParseResourceId(string? value, out string resourceId, out string subscriptionId, out string error)
    {
        resourceId = string.Empty;
        subscriptionId = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "resourceId darf nicht leer sein.";
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length > MaxResourceIdLength)
        {
            error = $"resourceId ist laenger als {MaxResourceIdLength} Zeichen.";
            return false;
        }

        if (!candidate.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            error = "resourceId muss mit '/subscriptions/' beginnen (keine URLs oder Hosts).";
            return false;
        }

        var segments = candidate.Split('/');          // erstes Element ist leer (fuehrender Slash)
        // ["", "subscriptions", "{guid}", "resourceGroups", "{rg}", "providers", "{ns}", "{type}", "{name}", ...]
        if (segments.Length < 9 || segments[^1].Length == 0 || segments.Any(s => s == "." || s == ".."))
        {
            error = "resourceId ist unvollstaendig oder enthaelt ungueltige Segmente.";
            return false;
        }

        if (!segments[3].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) ||
            !segments[5].Equals("providers", StringComparison.OrdinalIgnoreCase))
        {
            error = "resourceId muss dem Muster /subscriptions/{id}/resourceGroups/{name}/providers/{namespace}/{type}/{name} entsprechen.";
            return false;
        }

        // Anzahl der Segmente nach 'providers/{ns}' muss gerade sein (Typ/Name-Paare)
        if ((segments.Length - 7) % 2 != 0)
        {
            error = "resourceId enthaelt keine vollstaendigen Typ/Name-Paare.";
            return false;
        }

        for (var i = 4; i < segments.Length; i++)
        {
            if (i == 5)
            {
                continue; // 'providers'
            }

            if (!SegmentRegex().IsMatch(segments[i]))
            {
                error = "resourceId enthaelt unzulaessige Zeichen (erlaubt: Buchstaben, Ziffern, . _ ( ) ~ -).";
                return false;
            }
        }

        if (segments[6].Equals("Microsoft.ResourceHealth", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("/providers/Microsoft.ResourceHealth/", StringComparison.OrdinalIgnoreCase))
        {
            error = "resourceId muss die Ressource selbst bezeichnen, nicht einen Microsoft.ResourceHealth-Unterpfad.";
            return false;
        }

        if (!TryParseSubscriptionId(segments[2], out subscriptionId, out _))
        {
            error = "resourceId enthaelt keine gueltige Subscription-GUID.";
            return false;
        }

        resourceId = candidate;
        return true;
    }

    /// <summary>Parst einen ISO-8601-Zeitpunkt (UTC oder mit Offset) nach UTC. Leer = kein Filter.</summary>
    public static bool TryParseSinceUtc(string? value, DateTime nowUtc, out DateTime? sinceUtc, out string error)
    {
        sinceUtc = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            error = "sinceUtc muss ein ISO-8601-Zeitpunkt sein, z. B. 2026-10-01T00:00:00Z.";
            return false;
        }

        var utc = parsed.UtcDateTime;
        if (utc > nowUtc.AddMinutes(5))
        {
            error = "sinceUtc darf nicht in der Zukunft liegen.";
            return false;
        }

        sinceUtc = utc;
        return true;
    }

    /// <summary>Begrenzt maxResults auf 1..hardMax (Standard, wenn nicht angegeben).</summary>
    public static bool TryNormalizeMaxResults(int? value, int defaultValue, int hardMax, out int maxResults, out string error)
    {
        error = string.Empty;
        maxResults = value ?? defaultValue;

        if (maxResults < 1 || maxResults > hardMax)
        {
            error = $"maxResults muss zwischen 1 und {hardMax} liegen.";
            return false;
        }

        return true;
    }
}
