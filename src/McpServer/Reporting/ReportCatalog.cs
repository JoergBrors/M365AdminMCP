namespace McpServer.Reporting;

/// <summary>
/// Wie der Endpunkt parametrisiert wird (siehe jeweilige Microsoft-Graph-Beta-Dokumentation
/// unter reportRoot: https://learn.microsoft.com/graph/api/resources/reportroot?view=graph-rest-beta).
/// </summary>
public enum ReportParamMode
{
    /// <summary>Genau EIN Parameter: entweder period ODER date (mutually exclusive). Typisch für "...Detail"-Reports.</summary>
    PeriodOrDate,

    /// <summary>Nur period (D7/D30/D90/D180). Typisch für "...Counts"-Reports (Zeitreihen).</summary>
    PeriodOnly,

    /// <summary>Kein Parameter - liefert den aktuellen Zustand (z.B. Aktivierungsstatus).</summary>
    None,

    /// <summary>Sonderfall mit eigenen, optionalen Parametern (aktuell nur getApiUsage).</summary>
    Special
}

public record ReportDefinition(string FunctionName, string Category, string Description, ReportParamMode ParamMode);

/// <summary>
/// Vollständiger Katalog der Microsoft Graph BETA Adoption-/Usage-Report-Endpunkte unter reportRoot
/// (Stand: siehe docs/ARCHITECTURE.md, Quelle: offizielle Microsoft-Graph-Beta-Dokumentation).
/// Deckt alle Kategorien ab: Microsoft 365 Copilot, Forms, Teams (Device/User/Team-Activity), Outlook
/// (Activity/App Usage/Mailbox Usage), Microsoft 365 Activations/Active Users/Apps Usage/Browser Usage/
/// Groups Activity, Graph API Usage, OneDrive (Activity/Usage), SharePoint (Activity/Site Usage),
/// Skype for Business (Activity/Device Usage/Organizer/Participant/Peer-to-Peer), Viva Engage
/// (Activity/Device Usage/Groups Activity).
///
/// Beta-Endpunkte gelten laut Microsoft als "subject to change" und sind nicht für Produktion supported.
/// </summary>
public static class ReportCatalog
{
    public static readonly IReadOnlyDictionary<string, ReportDefinition> Reports = new List<ReportDefinition>
    {
        // --- Microsoft 365 Copilot usage ---
        new("getMicrosoft365CopilotUsageUserDetail", "Microsoft 365 Copilot usage", "Aktivitätsdaten je aktiviertem Copilot-Nutzer.", ReportParamMode.PeriodOrDate),
        new("getMicrosoft365CopilotUserCountSummary", "Microsoft 365 Copilot usage", "Aggregierte Anzahl aktiver/aktivierter Copilot-Nutzer im Zeitraum.", ReportParamMode.PeriodOnly),
        new("getMicrosoft365CopilotUserCountTrend", "Microsoft 365 Copilot usage", "Trend der täglichen Anzahl aktiver/aktivierter Copilot-Nutzer.", ReportParamMode.PeriodOnly),

        // --- Forms activity ---
        new("getFormsUserActivityUserDetail", "Forms activity", "Detaillierte Forms-Nutzung je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getFormsUserActivityUserCounts", "Forms activity", "Trend der aktiven Nutzer je Nutzertyp.", ReportParamMode.PeriodOnly),
        new("getFormsUserActivityCounts", "Forms activity", "Anzahl Aktivitäten je Aktivitätstyp im Zeitraum.", ReportParamMode.PeriodOnly),

        // --- Microsoft Teams device usage ---
        new("getTeamsDeviceUsageUserDetail", "Microsoft Teams device usage", "Teams-Gerätenutzung je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getTeamsDeviceUsageUserCounts", "Microsoft Teams device usage", "Tägliche eindeutige Nutzer je Gerätetyp.", ReportParamMode.PeriodOnly),
        new("getTeamsDeviceUsageTotalUserCounts", "Microsoft Teams device usage", "Tägliche eindeutige lizenzierte/nicht lizenzierte Nutzer je Gerätetyp.", ReportParamMode.PeriodOnly),
        new("getTeamsDeviceUsageDistributionUserCounts", "Microsoft Teams device usage", "Eindeutige Nutzer je Gerätetyp im gewählten Zeitraum.", ReportParamMode.PeriodOnly),
        new("getTeamsDeviceUsageDistributionTotalUserCounts", "Microsoft Teams device usage", "Eindeutige lizenzierte/nicht lizenzierte Nutzer je Gerätetyp im gewählten Zeitraum.", ReportParamMode.PeriodOnly),

        // --- Microsoft Teams user activity ---
        new("getTeamsUserActivityUserDetail", "Microsoft Teams user activity", "Teams-Nutzeraktivität je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getTeamsUserActivityCounts", "Microsoft Teams user activity", "Anzahl Teams-Aktivitäten je Aktivitätstyp (lizenzierte Nutzer).", ReportParamMode.PeriodOnly),
        new("getTeamsUserActivityTotalCounts", "Microsoft Teams user activity", "Anzahl Teams-Aktivitäten je Aktivitätstyp (lizenziert+nicht lizenziert).", ReportParamMode.PeriodOnly),
        new("getTeamsUserActivityUserCounts", "Microsoft Teams user activity", "Anzahl Nutzer je Aktivitätstyp (Chat, Anrufe, Meetings).", ReportParamMode.PeriodOnly),
        new("getTeamsUserActivityTotalUserCounts", "Microsoft Teams user activity", "Anzahl lizenzierter/nicht lizenzierter Nutzer je Aktivitätstyp.", ReportParamMode.PeriodOnly),
        new("getTeamsUserActivityDistributionUserCounts", "Microsoft Teams user activity", "Lizenzierte Nutzer je Aktivitätstyp im gewählten Zeitraum.", ReportParamMode.PeriodOnly),
        new("getTeamsUserActivityDistributionTotalUserCounts", "Microsoft Teams user activity", "Lizenzierte+nicht lizenzierte Nutzer je Aktivitätstyp im gewählten Zeitraum.", ReportParamMode.PeriodOnly),
        new("getTeamsUserActivityTotalDistributionCounts", "Microsoft Teams user activity", "Alle Teams-Nutzeraktivitäten (Nachrichten, Anrufe, Meetings, Audio/Video/Screen-Share-Dauer) im Zeitraum.", ReportParamMode.PeriodOnly),

        // --- Microsoft Teams team activity ---
        new("getTeamsTeamActivityDetail", "Microsoft Teams team activity", "Teams-Aktivität je Team.", ReportParamMode.PeriodOrDate),
        new("getTeamsTeamActivityCounts", "Microsoft Teams team activity", "Anzahl Team-Aktivitäten (Meetings/Nachrichten) across Teams.", ReportParamMode.PeriodOnly),
        new("getTeamsTeamActivityDistributionCounts", "Microsoft Teams team activity", "Team-Aktivitäten über den Zeitraum verteilt.", ReportParamMode.PeriodOnly),
        new("getTeamsTeamCounts", "Microsoft Teams team activity", "Anzahl Teams nach Typ.", ReportParamMode.PeriodOnly),

        // --- Outlook activity ---
        new("getEmailActivityUserDetail", "Outlook activity", "E-Mail-Aktivität je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getEmailActivityCounts", "Outlook activity", "Trend der E-Mail-Aktivität (gesendet/gelesen/empfangen).", ReportParamMode.PeriodOnly),
        new("getEmailActivityUserCounts", "Outlook activity", "Trend eindeutiger Nutzer mit E-Mail-Aktivität.", ReportParamMode.PeriodOnly),

        // --- Outlook app usage ---
        new("getEmailAppUsageUserDetail", "Outlook app usage", "Welche Aktivitäten Nutzer in welchen E-Mail-Apps durchgeführt haben.", ReportParamMode.PeriodOrDate),
        new("getEmailAppUsageAppsUserCounts", "Outlook app usage", "Eindeutige Nutzer je E-Mail-App.", ReportParamMode.PeriodOnly),
        new("getEmailAppUsageUserCounts", "Outlook app usage", "Eindeutige Nutzer, die sich per E-Mail-App mit Exchange Online verbunden haben.", ReportParamMode.PeriodOnly),
        new("getEmailAppUsageVersionsUserCounts", "Outlook app usage", "Eindeutige Nutzer je Outlook-Desktop-Version.", ReportParamMode.PeriodOnly),

        // --- Outlook mailbox usage ---
        new("getMailboxUsageDetail", "Outlook mailbox usage", "Details zur Postfachnutzung.", ReportParamMode.PeriodOrDate),
        new("getMailboxUsageMailboxCounts", "Outlook mailbox usage", "Gesamtzahl Postfächer und tägliche Aktivität.", ReportParamMode.PeriodOnly),
        new("getMailboxUsageQuotaStatusMailboxCounts", "Outlook mailbox usage", "Anzahl Postfächer je Kontingent-Kategorie.", ReportParamMode.PeriodOnly),
        new("getMailboxUsageStorage", "Outlook mailbox usage", "Genutzter Speicherplatz in der Organisation.", ReportParamMode.PeriodOnly),

        // --- Microsoft 365 activations ---
        new("getOffice365ActivationsUserDetail", "Microsoft 365 activations", "Nutzer, die Microsoft 365 aktiviert haben (aktueller Stand, kein Zeitraum).", ReportParamMode.None),
        new("getOffice365ActivationCounts", "Microsoft 365 activations", "Anzahl Aktivierungen auf Desktops/Geräten (aktueller Stand).", ReportParamMode.None),
        new("getOffice365ActivationsUserCounts", "Microsoft 365 activations", "Anzahl aktivierter vs. berechtigter Nutzer (aktueller Stand).", ReportParamMode.None),

        // --- Microsoft 365 active users (Adoption Kernreport) ---
        new("getOffice365ActiveUserDetail", "Microsoft 365 active users", "Aktive Microsoft-365-Nutzer je Dienst.", ReportParamMode.PeriodOrDate),
        new("getOffice365ActiveUserCounts", "Microsoft 365 active users", "Tägliche aktive Nutzer je Produkt im Zeitraum.", ReportParamMode.PeriodOnly),
        new("getOffice365ServicesUserCounts", "Microsoft 365 active users", "Anzahl Nutzer je Aktivitätstyp und Dienst.", ReportParamMode.PeriodOnly),

        // --- Microsoft 365 apps usage ---
        new("getM365AppUserDetail", "Microsoft 365 apps usage", "Nutzung von Microsoft 365 Apps je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getM365AppUserCounts", "Microsoft 365 apps usage", "Tägliche eindeutige Nutzer je App.", ReportParamMode.PeriodOnly),
        new("getM365AppPlatformUserCounts", "Microsoft 365 apps usage", "Tägliche eindeutige Nutzer je Plattform.", ReportParamMode.PeriodOnly),

        // --- Microsoft 365 browser usage ---
        new("getBrowserUserDetail", "Microsoft 365 browser usage", "Detaillierte Browsernutzung je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getBrowserUserCounts", "Microsoft 365 browser usage", "Trend aktiver Nutzer je Browser.", ReportParamMode.PeriodOnly),
        new("getBrowserDistributionUserCounts", "Microsoft 365 browser usage", "Nutzer je Browser im gewählten Zeitraum.", ReportParamMode.PeriodOnly),

        // --- Microsoft 365 groups activity ---
        new("getOffice365GroupsActivityDetail", "Microsoft 365 groups activity", "Microsoft-365-Gruppenaktivität je Gruppe.", ReportParamMode.PeriodOrDate),
        new("getOffice365GroupsActivityCounts", "Microsoft 365 groups activity", "Anzahl Gruppenaktivitäten across Workloads.", ReportParamMode.PeriodOnly),
        new("getOffice365GroupsActivityGroupCounts", "Microsoft 365 groups activity", "Tägliche Gesamtzahl Gruppen und Anteil aktiver Gruppen.", ReportParamMode.PeriodOnly),
        new("getOffice365GroupsActivityStorage", "Microsoft 365 groups activity", "Gesamter Speicherverbrauch aller Gruppenpostfächer/-sites.", ReportParamMode.PeriodOnly),
        new("getOffice365GroupsActivityFileCounts", "Microsoft 365 groups activity", "Gesamtzahl Dateien und aktive Dateien across Gruppen-Sites.", ReportParamMode.PeriodOnly),

        // --- Microsoft Graph API usage (Sonderfall mit eigenen Parametern) ---
        new("getApiUsage", "Microsoft Graph API usage", "Nutzung der Microsoft Graph API (optional gefiltert nach serviceArea/appId).", ReportParamMode.Special),

        // --- OneDrive activity ---
        new("getOneDriveActivityUserDetail", "OneDrive activity", "OneDrive-Aktivität je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getOneDriveActivityUserCounts", "OneDrive activity", "Trend aktiver OneDrive-Nutzer.", ReportParamMode.PeriodOnly),
        new("getOneDriveActivityFileCounts", "OneDrive activity", "Eindeutige lizenzierte Nutzer mit Dateiinteraktionen.", ReportParamMode.PeriodOnly),

        // --- OneDrive usage ---
        new("getOneDriveUsageAccountDetail", "OneDrive usage", "OneDrive-Nutzung je Konto.", ReportParamMode.PeriodOrDate),
        new("getOneDriveUsageAccountCounts", "OneDrive usage", "Trend aktiver OneDrive-Sites.", ReportParamMode.PeriodOnly),
        new("getOneDriveUsageFileCounts", "OneDrive usage", "Gesamtzahl Dateien und aktive Dateien across Sites.", ReportParamMode.PeriodOnly),
        new("getOneDriveUsageStorage", "OneDrive usage", "Trend des genutzten Speicherplatzes in OneDrive for Business.", ReportParamMode.PeriodOnly),

        // --- SharePoint activity ---
        new("getSharePointActivityUserDetail", "SharePoint activity", "SharePoint-Aktivität je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getSharePointActivityFileCounts", "SharePoint activity", "Eindeutige lizenzierte Nutzer mit Dateiinteraktion auf SharePoint-Sites.", ReportParamMode.PeriodOnly),
        new("getSharePointActivityUserCounts", "SharePoint activity", "Trend aktiver SharePoint-Nutzer.", ReportParamMode.PeriodOnly),
        new("getSharePointActivityPages", "SharePoint activity", "Anzahl eindeutig besuchter Seiten.", ReportParamMode.PeriodOnly),

        // --- SharePoint site usage ---
        new("getSharePointSiteUsageDetail", "SharePoint site usage", "Details zur SharePoint-Site-Nutzung.", ReportParamMode.PeriodOrDate),
        new("getSharePointSiteUsageFileCounts", "SharePoint site usage", "Gesamtzahl Dateien und aktive Dateien across Sites.", ReportParamMode.PeriodOnly),
        new("getSharePointSiteUsageSiteCounts", "SharePoint site usage", "Trend Gesamt-/aktiver Site-Anzahl.", ReportParamMode.PeriodOnly),
        new("getSharePointSiteUsageStorage", "SharePoint site usage", "Trend zugewiesener/genutzter Speicherplatz.", ReportParamMode.PeriodOnly),
        new("getSharePointSiteUsagePages", "SharePoint site usage", "Seitenaufrufe across alle Sites.", ReportParamMode.PeriodOnly),

        // --- Skype for Business activity ---
        new("getSkypeForBusinessActivityUserDetail", "Skype for Business activity", "Skype-for-Business-Aktivität je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getSkypeForBusinessActivityCounts", "Skype for Business activity", "Trend organisierter/teilgenommener Konferenzsitzungen.", ReportParamMode.PeriodOnly),
        new("getSkypeForBusinessActivityUserCounts", "Skype for Business activity", "Trend eindeutiger Nutzer mit Konferenzsitzungen.", ReportParamMode.PeriodOnly),

        // --- Skype for Business device usage ---
        new("getSkypeForBusinessDeviceUsageUserDetail", "Skype for Business device usage", "Gerätenutzung je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getSkypeForBusinessDeviceUsageDistributionUserCounts", "Skype for Business device usage", "Nutzer je Gerätetyp.", ReportParamMode.PeriodOnly),
        new("getSkypeForBusinessDeviceUsageUserCounts", "Skype for Business device usage", "Trend verbundener Nutzer je Gerätetyp.", ReportParamMode.PeriodOnly),

        // --- Skype for Business organizer activity ---
        new("getSkypeForBusinessOrganizerActivityCounts", "Skype for Business organizer activity", "Anzahl/Art organisierter Konferenzsitzungen.", ReportParamMode.PeriodOnly),
        new("getSkypeForBusinessOrganizerActivityUserCounts", "Skype for Business organizer activity", "Eindeutige Nutzer, die Konferenzsitzungen organisiert haben.", ReportParamMode.PeriodOnly),
        new("getSkypeForBusinessOrganizerActivityMinuteCounts", "Skype for Business organizer activity", "Minutenlänge organisierter Konferenzsitzungen.", ReportParamMode.PeriodOnly),

        // --- Skype for Business participant activity ---
        new("getSkypeForBusinessParticipantActivityCounts", "Skype for Business participant activity", "Anzahl/Art Konferenzsitzungen, an denen teilgenommen wurde.", ReportParamMode.PeriodOnly),
        new("getSkypeForBusinessParticipantActivityUserCounts", "Skype for Business participant activity", "Eindeutige Teilnehmer an Konferenzsitzungen.", ReportParamMode.PeriodOnly),
        new("getSkypeForBusinessParticipantActivityMinuteCounts", "Skype for Business participant activity", "Minutenlänge der Teilnahme an Konferenzsitzungen.", ReportParamMode.PeriodOnly),

        // --- Skype for Business peer-to-peer activity ---
        new("getSkypeForBusinessPeerToPeerActivityCounts", "Skype for Business peer-to-peer activity", "Anzahl/Art Peer-to-Peer-Sitzungen.", ReportParamMode.PeriodOnly),
        new("getSkypeForBusinessPeerToPeerActivityUserCounts", "Skype for Business peer-to-peer activity", "Eindeutige Nutzer mit Peer-to-Peer-Sitzungen.", ReportParamMode.PeriodOnly),
        new("getSkypeForBusinessPeerToPeerActivityMinuteCounts", "Skype for Business peer-to-peer activity", "Minutenlänge von Peer-to-Peer-Sitzungen.", ReportParamMode.PeriodOnly),

        // --- Viva Engage (Yammer) activity ---
        new("getYammerActivityUserDetail", "Viva Engage activity", "Viva-Engage-Aktivität je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getYammerActivityCounts", "Viva Engage activity", "Trend geposteter/gelesener/gelikter Nachrichten.", ReportParamMode.PeriodOnly),
        new("getYammerActivityUserCounts", "Viva Engage activity", "Trend eindeutiger Nutzer mit Viva-Engage-Aktivität.", ReportParamMode.PeriodOnly),

        // --- Viva Engage device usage ---
        new("getYammerDeviceUsageUserDetail", "Viva Engage device usage", "Gerätenutzung je Nutzer.", ReportParamMode.PeriodOrDate),
        new("getYammerDeviceUsageDistributionUserCounts", "Viva Engage device usage", "Nutzer je Gerätetyp.", ReportParamMode.PeriodOnly),
        new("getYammerDeviceUsageUserCounts", "Viva Engage device usage", "Tägliche Nutzer je Gerätetyp.", ReportParamMode.PeriodOnly),

        // --- Viva Engage groups activity ---
        new("getYammerGroupsActivityDetail", "Viva Engage groups activity", "Viva-Engage-Gruppenaktivität je Gruppe.", ReportParamMode.PeriodOrDate),
        new("getYammerGroupsActivityGroupCounts", "Viva Engage groups activity", "Gesamtzahl Gruppen und Anteil mit Konversationsaktivität.", ReportParamMode.PeriodOnly),
        new("getYammerGroupsActivityCounts", "Viva Engage groups activity", "Anzahl geposteter/gelesener/gelikter Nachrichten in Gruppen.", ReportParamMode.PeriodOnly),

    }.ToDictionary(r => r.FunctionName, r => r, StringComparer.OrdinalIgnoreCase);

    public static readonly string[] AllowedPeriods = { "D7", "D30", "D90", "D180" };
}
