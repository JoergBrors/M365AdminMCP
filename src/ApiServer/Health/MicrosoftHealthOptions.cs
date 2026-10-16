using McpServer.Utils;
using Microsoft.Extensions.Options;

namespace ApiServer.Health;

/// <summary>
/// Konfiguration der Microsoft-Health-Erweiterung (Intune/Microsoft 365 ueber Graph, Azure Service/Resource Health ueber ARM).
/// Konfigurationsabschnitt "MicrosoftHealth"; in Azure als App Settings "MicrosoftHealth__...".
/// </summary>
public class MicrosoftHealthOptions
{
    public const string SectionName = "MicrosoftHealth";

    /// <summary>Hauptschalter. Bei false liefern alle Health-Endpunkte den Status "disabled".</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional. Wenn gesetzt, muss sie der Tenant-ID aus AzureAd:TenantId entsprechen (kein tenantuebergreifender Zugriff).</summary>
    public string? TenantId { get; set; }

    public bool EnableIntune { get; set; } = true;

    /// <summary>Azure-Abfragen sind zusaetzlich nur fuer Subscriptions aus AllowedSubscriptionIds moeglich.</summary>
    public bool EnableAzure { get; set; } = true;

    /// <summary>Allowlist der abfragbaren Azure-Subscriptions (GUIDs). Leer = Azure-Abfragen sind deaktiviert.</summary>
    public string[] AllowedSubscriptionIds { get; set; } = [];

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Standard fuer maxResults, wenn der Aufrufer nichts angibt.</summary>
    public int DefaultMaxResults { get; set; } = HealthInputValidator.DefaultMaxResults;

    /// <summary>Harte Obergrenze fuer maxResults.</summary>
    public int MaxResults { get; set; } = HealthInputValidator.HardMaxResults;

    /// <summary>Maximale Anzahl Seiten je Abfrage (Schutz vor unbegrenzter Pagination).</summary>
    public int MaxPages { get; set; } = 20;
}

/// <summary>Startvalidierung: fehlerhafte Konfiguration bricht den Start mit klarer Meldung ab.</summary>
public sealed class MicrosoftHealthOptionsValidator(IConfiguration configuration) : IValidateOptions<MicrosoftHealthOptions>
{
    public ValidateOptionsResult Validate(string? name, MicrosoftHealthOptions options)
    {
        var errors = new List<string>();

        if (options.RequestTimeoutSeconds is < 1 or > 300)
        {
            errors.Add("MicrosoftHealth:RequestTimeoutSeconds muss zwischen 1 und 300 liegen.");
        }

        if (options.MaxResults is < 1 or > HealthInputValidator.HardMaxResults)
        {
            errors.Add($"MicrosoftHealth:MaxResults muss zwischen 1 und {HealthInputValidator.HardMaxResults} liegen.");
        }

        if (options.DefaultMaxResults < 1 || options.DefaultMaxResults > options.MaxResults)
        {
            errors.Add("MicrosoftHealth:DefaultMaxResults muss zwischen 1 und MicrosoftHealth:MaxResults liegen.");
        }

        if (options.MaxPages is < 1 or > 100)
        {
            errors.Add("MicrosoftHealth:MaxPages muss zwischen 1 und 100 liegen.");
        }

        foreach (var id in options.AllowedSubscriptionIds)
        {
            if (!HealthInputValidator.TryParseSubscriptionId(id, out _, out _))
            {
                errors.Add("MicrosoftHealth:AllowedSubscriptionIds enthaelt einen Eintrag, der keine gueltige GUID ist.");
                break; // Wert nicht in die Meldung uebernehmen
            }
        }

        var configuredTenant = options.TenantId;
        var appTenant = configuration["AzureAd:TenantId"];
        if (!string.IsNullOrWhiteSpace(configuredTenant) && !string.IsNullOrWhiteSpace(appTenant) &&
            !string.Equals(configuredTenant.Trim(), appTenant.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("MicrosoftHealth:TenantId stimmt nicht mit AzureAd:TenantId ueberein (tenantuebergreifende Abfragen sind nicht erlaubt).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
