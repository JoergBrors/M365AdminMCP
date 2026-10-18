using System.Globalization;
using System.Text.RegularExpressions;

namespace McpServer.Utils;

/// <summary>
/// Eingabevalidierung fuer search_service_health_issues / get_service_health_issue / get_service_health_issue_content.
/// Wird im McpServer (lesbare Fehler fuer das LLM) und im ApiServer (verbindlich, per Compile-Link, ADR-0009) verwendet.
/// Es werden nie beliebige OData-Ausdruecke akzeptiert: jede Eingabe ist ein einzelner, streng geprueften Wert.
/// </summary>
public static partial class IssueInputValidator
{
    public const int DefaultMaxResults = 25;
    public const int HardMaxResults = 100;
    public const int MaxTextLength = 100;

    public static readonly string[] Classifications = ["incident", "advisory"];
    public static readonly string[] Projections = ["summary", "details", "full"];

    // Dokumentierte serviceHealthStatus-Werte (Microsoft Learn); "reserved for future use" ist bewusst eingeschlossen.
    public static readonly string[] Statuses =
    [
        "serviceOperational", "investigating", "restoringService", "verifyingService", "serviceRestored", "postIncidentReviewPublished",
        "serviceDegradation", "serviceInterruption", "extendedRecovery", "falsePositive", "investigationSuspended",
        "resolved", "mitigatedExternal", "mitigated", "resolvedExternal", "confirmed", "reported"
    ];

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^issue:([A-Za-z0-9_\-]{1,64}):(impact|details|post:([0-9]{1,4}))$", RegexOptions.CultureInvariant)]
    private static partial Regex ContentIdRegex();

    public static bool TryNormalizeMaxResults(int? value, out int max, out string error)
    {
        max = value ?? DefaultMaxResults;
        error = string.Empty;
        if (max is < 1 or > HardMaxResults)
        {
            error = $"maxResults muss zwischen 1 und {HardMaxResults} liegen.";
            return false;
        }

        return true;
    }

    /// <summary>Freitext (service, searchText): getrimmt, begrenzte Laenge, keine Steuerzeichen. Leer = nicht gesetzt.</summary>
    public static bool TryNormalizeText(string? value, string name, out string? normalized, out string error)
    {
        normalized = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        if (text.Length > MaxTextLength || text.Any(char.IsControl))
        {
            error = $"{name} ist ungueltig (max. {MaxTextLength} Zeichen, keine Steuerzeichen).";
            return false;
        }

        normalized = text;
        return true;
    }

    public static bool TryParseClassification(string? value, out string? classification, out string error) =>
        TryParseEnum(value, Classifications, "classification", out classification, out error);

    public static bool TryParseStatus(string? value, out string? status, out string error) =>
        TryParseEnum(value, Statuses, "status", out status, out error);

    public static bool TryParseProjection(string? value, out string projection, out string error)
    {
        var ok = TryParseEnum(string.IsNullOrWhiteSpace(value) ? "summary" : value, Projections, "projection", out var p, out error);
        projection = p ?? "summary";
        return ok;
    }

    private static bool TryParseEnum(string? value, string[] allowed, string name, out string? result, out string error)
    {
        result = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        result = allowed.FirstOrDefault(a => string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (result is null)
        {
            error = $"{name} ist ungueltig. Erlaubt: {string.Join(", ", allowed)}.";
            return false;
        }

        return true;
    }

    /// <summary>ISO-8601-Zeitpunkt (UTC bevorzugt); Datum ohne Zone gilt als UTC.</summary>
    public static bool TryParseDate(string? value, string name, out DateTimeOffset? date, out string error)
    {
        date = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (value.Length > 40 || !DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            error = $"{name} ist kein gueltiger ISO-8601-Zeitpunkt (z. B. 2026-10-01T00:00:00Z).";
            return false;
        }

        date = parsed;
        return true;
    }

    public static bool TryParseIssueId(string? value, out string id, out string error)
    {
        id = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !IdRegex().IsMatch(value.Trim()))
        {
            error = "id ist ungueltig (erlaubt: Buchstaben, Ziffern, '_' und '-', max. 64 Zeichen, z. B. EX226792).";
            return false;
        }

        id = value.Trim();
        return true;
    }

    /// <summary>contentId hat die feste Form issue:{id}:impact | issue:{id}:details | issue:{id}:post:{n}; es gibt keine URLs oder Pfade.</summary>
    public static bool TryParseContentId(string? value, out string issueId, out string kind, out int postIndex, out string error)
    {
        issueId = string.Empty;
        kind = string.Empty;
        postIndex = -1;
        error = string.Empty;

        var match = value is { Length: <= 100 } ? ContentIdRegex().Match(value.Trim()) : null;
        if (match is not { Success: true })
        {
            error = "contentId ist ungueltig. Erwartet: contentId aus einem Suchergebnis (z. B. issue:EX226792:post:0).";
            return false;
        }

        issueId = match.Groups[1].Value;
        if (match.Groups[3].Success)
        {
            kind = "post";
            postIndex = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            kind = match.Groups[2].Value;
        }

        return true;
    }
}
