using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace ApiServer.Services;

/// <summary>
/// Konfiguration "ResponseLimits". Die MCP-Clients begrenzen ein einzelnes SSE-Event (Python-SDK/httpx2: 1 MiB, beobachtet);
/// das Limit hier liegt bewusst deutlich darunter (Reserve fuer JSON-RPC-Rahmen und erneutes Escaping im Tool-Ergebnis).
/// </summary>
public sealed class ResponseLimitsOptions
{
    public const string SectionName = "ResponseLimits";

    /// <summary>Maximale Groesse einer Tool-Antwort in Bytes, gemessen als UTF-8 des JSON-escapten Antworttexts.</summary>
    public int MaxResponseBytes { get; set; } = 700_000;

    /// <summary>Maximale Groesse eines Content-Chunks (get_service_health_issue_content); muss unter MaxResponseBytes liegen.</summary>
    public int MaxChunkBytes { get; set; } = 400_000;

    /// <summary>Gueltigkeit eines Fortsetzungs-Tokens in Minuten.</summary>
    public int ContinuationTokenMinutes { get; set; } = 15;

    /// <summary>
    /// Dedizierter Signaturschluessel fuer Fortsetzungs-Tokens (mind. 32 Zeichen). Azure: App Setting
    /// <c>ResponseLimits__ContinuationSigningKey</c> als Key-Vault-Referenz. Pflicht ausserhalb von Development.
    /// </summary>
    public string? ContinuationSigningKey { get; set; }
}

/// <summary>Startvalidierung: ausserhalb von Development sind ein gueltiger Schluessel und sinnvolle Limits Pflicht (Start bricht ab).</summary>
public sealed class ResponseLimitsOptionsValidator(IHostEnvironment environment) : IValidateOptions<ResponseLimitsOptions>
{
    public const int MinKeyLength = 32;

    /// <summary>Gueltig: mind. 32 Zeichen, kein Platzhalter und keine unaufgeloeste Key-Vault-Referenz.</summary>
    public static bool IsValidKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length >= MinKeyLength &&
        !key.StartsWith("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase) && !key.StartsWith("__", StringComparison.Ordinal);

    public ValidateOptionsResult Validate(string? name, ResponseLimitsOptions options)
    {
        var errors = new List<string>();

        if (options.MaxResponseBytes is < 10_000 or > 900_000)
            errors.Add("ResponseLimits:MaxResponseBytes muss zwischen 10000 und 900000 liegen (Client-Limit 1 MiB je SSE-Event).");
        if (options.MaxChunkBytes < 1_000 || options.MaxChunkBytes >= options.MaxResponseBytes)
            errors.Add("ResponseLimits:MaxChunkBytes muss zwischen 1000 und MaxResponseBytes liegen.");

        if (!environment.IsDevelopment() && !IsValidKey(options.ContinuationSigningKey))
            errors.Add($"ResponseLimits:ContinuationSigningKey fehlt oder ist ungueltig (mind. {MinKeyLength} Zeichen; eine unaufgeloeste " +
                       "Key-Vault-Referenz zaehlt als ungueltig). Der Schluessel wird von Terraform im Key Vault erzeugt; Managed Identity " +
                       "braucht 'Key Vault Secrets User'. Der Wert wird hier nie ausgegeben.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>
/// Begrenzt Antworten auf eine sichere Groesse. Gemessen wird die Groesse, die der Antworttext als JSON-String im MCP-Ergebnis
/// hat (UTF-8, mit Escaping; Standard-Encoder = konservativ: Nicht-ASCII wird als \uXXXX gezaehlt).
/// Zu grosse Collections werden nie stillschweigend gekuerzt: die Antwort enthaelt truncated/isPartial, Gruende und Hinweise.
/// </summary>
public static class ResponseSizeGuard
{
    public static int EscapedSize(string text) => JsonSerializer.SerializeToUtf8Bytes(text).Length;

    /// <summary>Gibt <paramref name="json"/> unveraendert zurueck, wenn es passt; sonst eine strukturierte, markierte Teilantwort.</summary>
    public static string Enforce(string json, int limitBytes, string source, string hint)
    {
        var size = EscapedSize(json);
        if (size <= limitBytes)
        {
            return json;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Envelope(source, size, limitBytes, hint, [], 0, []);
        }

        using (document)
        {
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v
                : default;

            if (array.ValueKind != JsonValueKind.Array)
            {
                return Envelope(source, size, limitBytes, hint, [], 0, []);
            }

            var items = array.EnumerateArray().Select(e => e.GetRawText()).ToList();
            var ids = array.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                .Where(id => id is not null).Cast<string>().Take(50).ToList();

            // Groesstes k, fuer das die Antwort noch passt (Groesse waechst monoton mit k).
            int lo = 0, hi = items.Count;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (EscapedSize(Envelope(source, size, limitBytes, hint, items.Take(mid).ToList(), items.Count, ids)) <= limitBytes)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return Envelope(source, size, limitBytes, hint, items.Take(lo).ToList(), items.Count, ids);
        }
    }

    private static string Envelope(string source, int originalSize, int limit, string hint, IReadOnlyList<string> rawItems, int totalItems, IReadOnlyList<string> ids)
    {
        var sb = new StringBuilder();
        sb.Append("{\"truncated\":true,\"isPartial\":true,\"source\":").Append(JsonSerializer.Serialize(source));
        sb.Append(",\"truncationReason\":").Append(JsonSerializer.Serialize(
            $"Antwort ({originalSize} Bytes) ueberschreitet das Limit von {limit} Bytes; nur {rawItems.Count} von {totalItems} Eintraegen enthalten."));
        sb.Append(",\"originalSizeBytes\":").Append(originalSize);
        sb.Append(",\"limitBytes\":").Append(limit);
        sb.Append(",\"returnedItems\":").Append(rawItems.Count);
        sb.Append(",\"totalItems\":").Append(totalItems);
        sb.Append(",\"hint\":").Append(JsonSerializer.Serialize(hint));
        sb.Append(",\"availableIds\":").Append(JsonSerializer.Serialize(ids));
        sb.Append(",\"value\":[").Append(string.Join(",", rawItems)).Append("]}");
        return sb.ToString();
    }
}
