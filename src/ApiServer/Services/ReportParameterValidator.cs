using System.Globalization;
using System.Text.RegularExpressions;

namespace ApiServer.Services;

/// <summary>
/// Strikte Validierung der Report-Parameter, die in den Graph-Pfad interpoliert werden (ADR-0008, Luecke "date/serviceArea/appId").
/// Ohne diese Pruefung koennte ein Aufrufer ueber Sonderzeichen den Graph-Pfad veraendern (Path-/Function-Injection).
/// </summary>
public static partial class ReportParameterValidator
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$")]
    private static partial Regex ServiceAreaPattern();

    /// <summary>Erlaubt ausschliesslich yyyy-MM-dd (echtes Kalenderdatum).</summary>
    public static string ValidateDate(string date)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new ArgumentException("Ungueltiger date-Wert. Erwartet: yyyy-MM-dd.");
        }

        return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Erlaubt ausschliesslich eine GUID im Format "D" (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).</summary>
    public static string ValidateAppId(string appId)
    {
        if (!Guid.TryParseExact(appId, "D", out var guid))
        {
            throw new ArgumentException("Ungueltiger appId-Wert. Erwartet: GUID.");
        }

        return guid.ToString("D");
    }

    /// <summary>Erlaubt nur Buchstaben, Ziffern, Leerzeichen, Punkt, Unterstrich und Bindestrich (max. 64 Zeichen).</summary>
    public static string ValidateServiceArea(string serviceArea)
    {
        if (!ServiceAreaPattern().IsMatch(serviceArea))
        {
            throw new ArgumentException("Ungueltiger serviceArea-Wert. Erlaubt: Buchstaben, Ziffern, Leerzeichen, '.', '_', '-' (max. 64 Zeichen).");
        }

        return serviceArea;
    }
}
